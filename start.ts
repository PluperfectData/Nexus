// Starts Nexus on this computer, with its own MySQL server and Mailpit, the service, and its pages, and opens it in the
// browser. The Nexus shortcut that install.ps1 puts on the desktop runs it. Ctrl+C, or closing the window, stops them all.
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { randomBytes, X509Certificate } from 'node:crypto'
import { createReadStream, createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from 'node:fs'
import { createServer, get } from 'node:https'
import { dirname, extname, join, normalize, resolve, sep } from 'node:path'
import { stdin } from 'node:process'
import { setTimeout as sleep } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'

const SITE_PORT = 6165
const API_PORT = 6163
const CALLBACK_PORT = 6164
const DATABASE_PORT = 6167
const DATABASE = 'nexus'
const MAILPIT_PORT = 6168
const SITE = `https://localhost:${SITE_PORT}`
const API = `https://localhost:${API_PORT}`

const root = dirname(fileURLToPath(import.meta.url))
const data = join(root, 'data')
const app = join(data, 'app')
const logs = join(data, 'logs')
const datadir = join(data, 'mysql')
const clientFile = join(data, 'mysql-client.cnf')
// The .NET development certificate, which install.ps1 trusts and exports here for the pages and the service.
const certificateFile = join(data, 'certificate.pem')
const certificateKeyFile = join(data, 'certificate.key')

/** The programs the installer found, so this does not search for them again. */
type Tools = { dotnet: string; mysqlBin: string; mailpit: string | null }

type Secrets = { rootPassword: string; databasePassword: string }

/** A window opened from the shortcut closes when this exits, so a failure waits to be read. */
function stopAfterReading(message: string, code = 1): void {
  console.error(`\n${message}\nPress Enter to close this window.`)
  stdin.once('data', () => process.exit(code))
}

function writeAtomically(file: string, text: string): void {
  const temp = `${file}.${process.pid}.tmp`
  writeFileSync(temp, text, { mode: 0o600 })
  renameSync(temp, file)
}

function run(command: string, args: string[]): { ok: boolean; output: string } {
  const result = spawnSync(command, args, { encoding: 'utf8' })
  return { ok: result.status === 0, output: `${result.stdout ?? ''}${result.stderr ?? ''}`.trim() }
}

/** Whether the address answers 200, checked against the certificate this copy serves with. */
function answering(url: string, ca: string): Promise<boolean> {
  return new Promise((done) => {
    const request = get(url, { ca, timeout: 3000 }, (response) => {
      response.resume()
      done(response.statusCode === 200)
    })
    request.on('timeout', () => request.destroy())
    request.on('error', () => done(false))
  })
}

function openBrowser(): void {
  spawn('cmd', ['/c', 'start', '', SITE], { stdio: 'ignore', detached: true }).unref()
}

/** The database passwords, made the first time and kept in data/secrets.json. */
function secrets(): Secrets {
  const file = join(data, 'secrets.json')
  if (existsSync(file)) return JSON.parse(readFileSync(file, 'utf8')) as Secrets
  const made: Secrets = { rootPassword: randomBytes(24).toString('base64url'), databasePassword: randomBytes(24).toString('base64url') }
  writeAtomically(file, JSON.stringify(made, null, 2) + '\n')
  return made
}

/** A MySQL server of Nexus's own in data/mysql, on its own port, so it never touches another MySQL. */
async function startDatabase(tools: Tools, passwords: Secrets): Promise<void> {
  const bin = (name: string) => join(tools.mysqlBin, `${name}.exe`)
  // --defaults-extra-file must come first, and keeps the root password off the command line.
  writeAtomically(clientFile, `[client]\nuser=root\npassword=${passwords.rootPassword}\nhost=127.0.0.1\nport=${DATABASE_PORT}\n`)
  const asRoot = (tool: string, args: string[]) => run(bin(tool), [`--defaults-extra-file=${clientFile}`, ...args])

  const fresh = !existsSync(join(datadir, 'mysql.ibd'))
  if (fresh) {
    console.log("Creating Nexus's database server in data/mysql...")
    const made = run(bin('mysqld'), ['--initialize-insecure', `--datadir=${datadir}`, '--console'])
    if (!made.ok) throw new Error(`mysqld could not create data/mysql:\n${made.output}`)
  }

  if (!asRoot('mysqladmin', ['ping']).ok) {
    const server = spawn(bin('mysqld'), [`--datadir=${datadir}`, `--port=${DATABASE_PORT}`, '--bind-address=127.0.0.1', '--mysqlx=OFF',
      `--log-error=${join(logs, 'mysql.log')}`], { stdio: 'ignore', detached: true })
    server.unref()
    let up = false
    for (let waited = 0; waited < 60_000 && !up; waited += 1000) {
      await sleep(1000)
      up = run(bin('mysqladmin'), ['--host=127.0.0.1', `--port=${DATABASE_PORT}`, 'ping']).ok
    }
    if (!up) throw new Error('The database server did not start. data/logs/mysql.log says why.')
  }

  // A server made with --initialize-insecure has a root with no password until this sets one.
  run(bin('mysql'), ['--host=127.0.0.1', `--port=${DATABASE_PORT}`, '--user=root', '--skip-password', '-e',
    `ALTER USER 'root'@'localhost' IDENTIFIED BY '${passwords.rootPassword}'`])
  const made = asRoot('mysql', ['-e', [
    `CREATE DATABASE IF NOT EXISTS \`${DATABASE}\``,
    `CREATE USER IF NOT EXISTS '${DATABASE}'@'localhost' IDENTIFIED BY '${passwords.databasePassword}'`,
    `ALTER USER '${DATABASE}'@'localhost' IDENTIFIED BY '${passwords.databasePassword}'`,
    `GRANT ALL PRIVILEGES ON \`${DATABASE}\`.* TO '${DATABASE}'@'localhost'`,
  ].join('; ')])
  if (!made.ok) throw new Error(`The database server did not accept Nexus's database and user:\n${made.output}`)
}

/** Mailpit, which keeps the mail Nexus sends for reading on its own page, with its inbox in data/mailpit.db. */
function startMailpit(tools: Tools): ChildProcess | null {
  if (!tools.mailpit) {
    console.warn('Mailpit is not installed, so Nexus sends no mail until its Settings page sets up Postmark. Run install.ps1 again to install it.')
    return null
  }
  const mailpit = spawn(tools.mailpit, ['--listen', `127.0.0.1:${MAILPIT_PORT}`, '--smtp', '127.0.0.1:0', '--database', join(data, 'mailpit.db')], {
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  const log = createWriteStream(join(logs, 'mailpit.log'), { flags: 'a' })
  mailpit.stdout?.pipe(log)
  mailpit.stderr?.pipe(log)
  return mailpit
}

function stopDatabase(tools: Tools): void {
  run(join(tools.mysqlBin, 'mysqladmin.exe'), [`--defaults-extra-file=${clientFile}`, 'shutdown'])
}

const TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp', '.ico': 'image/x-icon',
  '.woff2': 'font/woff2', '.txt': 'text/plain; charset=utf-8',
}

/** Serves the built pages, and index.html for any path that is not a file, since the pages route in the browser. */
function servePages(folder: string, cert: string, key: string) {
  const server = createServer({ cert, key }, (request, response) => {
    const path = decodeURIComponent(new URL(request.url ?? '/', SITE).pathname)
    let file = normalize(join(folder, path))
    if (!file.startsWith(folder + sep) || !existsSync(file) || statSync(file).isDirectory()) file = join(folder, 'index.html')
    response.writeHead(200, {
      'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream',
      'Cache-Control': file.includes(`${sep}assets${sep}`) ? 'public, max-age=31536000, immutable' : 'no-cache',
    })
    createReadStream(file).pipe(response)
  })
  server.listen(SITE_PORT, '127.0.0.1')
  return server
}

/** Passes the service's output through, and calls out the installer's address when the service prints one. */
function echo(child: ChildProcess): void {
  for (const stream of [child.stdout, child.stderr]) {
    stream?.on('data', (chunk: Buffer) => {
      const text = chunk.toString()
      process.stdout.write(text)
      const page = /Open (\S+\/install\S*) to set it up/.exec(text)?.[1]
      if (page) console.log(`\n  Nexus is not set up yet. Open ${page} to set it up.\n`)
    })
  }
}

async function main(): Promise<void> {
  const toolsFile = join(data, 'install.json')
  const installed = [toolsFile, certificateFile, certificateKeyFile, join(app, 'api', 'Nexus.Service.dll'), join(app, 'site', 'index.html')]
  if (!installed.every((file) => existsSync(file))) {
    stopAfterReading('Nexus is not installed yet. Run install.ps1 first, as the README says.')
    return
  }
  const cert = readFileSync(certificateFile, 'utf8')
  const expires = new Date(new X509Certificate(cert).validTo)
  if (expires.getTime() < Date.now()) {
    stopAfterReading(`The certificate Nexus serves its addresses with expired on ${expires.toLocaleDateString()}. Run install.ps1 again to renew it.`)
    return
  }
  if ((await answering(`${API}/health`, cert)) && (await answering(SITE, cert))) {
    console.log(`Nexus is already running at ${SITE}.`)
    openBrowser()
    return
  }
  const tools = JSON.parse(readFileSync(toolsFile, 'utf8')) as Tools
  mkdirSync(logs, { recursive: true })
  const passwords = secrets()
  await startDatabase(tools, passwords)
  const mailpit = startMailpit(tools)

  const api = spawn(tools.dotnet, [join(app, 'api', 'Nexus.Service.dll')], {
    cwd: join(app, 'api'),
    stdio: ['ignore', 'pipe', 'pipe'],
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: `${API};https://127.0.0.1:${CALLBACK_PORT}`,
      Kestrel__Certificates__Default__Path: certificateFile,
      Kestrel__Certificates__Default__KeyPath: certificateKeyFile,
      ConnectionStrings__Nexus: `Server=127.0.0.1;Port=${DATABASE_PORT};Database=${DATABASE};User ID=${DATABASE};Password=${passwords.databasePassword}`,
      Nexus__ClientOrigin: SITE,
      Nexus__CallbackOrigin: `https://127.0.0.1:${CALLBACK_PORT}`,
      Nexus__DataDirectory: join(data, 'api'),
      Mail__MailpitUrl: mailpit ? `http://127.0.0.1:${MAILPIT_PORT}` : '',
    },
  })
  echo(api)
  const pages = servePages(resolve(app, 'site'), cert, readFileSync(certificateKeyFile, 'utf8'))

  let stopping = false
  const stop = (code: number) => {
    if (stopping) return
    stopping = true
    pages.close()
    api.kill()
    mailpit?.kill()
    stopDatabase(tools)
    process.exit(code)
  }
  process.on('SIGINT', () => stop(0))
  process.on('SIGTERM', () => stop(0))
  process.on('SIGHUP', () => stop(0))
  api.on('exit', (code) => {
    if (stopping) return
    stopping = true
    pages.close()
    mailpit?.kill()
    stopDatabase(tools)
    stopAfterReading(`The service stopped (exit code ${code}). The lines above, and the logs in data/api/logs, say why.`, code || 1)
  })

  // The first start brings a new database up to date, which takes a minute or two.
  for (let waited = 0; waited < 300_000; waited += 1000) {
    await sleep(1000)
    if (await answering(`${API}/health`, cert)) {
      console.log(`\nNexus is running at ${SITE}. Keep this window open while you use it; Ctrl+C or closing it stops Nexus.\n`)
      openBrowser()
      return
    }
  }
  console.warn('The service has not answered after five minutes. The lines above say what it is doing.')
}

main().catch((e: Error) => stopAfterReading(e.message))
