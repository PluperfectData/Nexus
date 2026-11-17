import { type MailActions, saveMail, type Settings, testMail, turnOffMail } from './api'

/** The Settings page's mail calls, which mail the signed-in admin to test. */
export const settingsMail = (onSaved: (settings: Settings) => void): MailActions => ({
  save: async (serverToken, from, fromName) => onSaved(await saveMail(serverToken, from, fromName)),
  off: async () => onSaved(await turnOffMail()),
  test: () => testMail(),
  asksForRecipient: false,
})
