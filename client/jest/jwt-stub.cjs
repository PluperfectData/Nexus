module.exports = {
  createAuthClient: () => () => Promise.reject(new Error('No service in unit tests.')),
}
