using Nexus.Schwab.SignIn;

var reading = args is ["read", ..];
// Signing in waits on a person in the browser, so it gives up after a while; a read runs until it finishes.
using var cancel = reading ? new CancellationTokenSource() : new CancellationTokenSource(SignInFlow.Timeout);
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    cancel.Cancel();
};

return reading ? await ReadCheck.RunAsync(args[1..], cancel.Token) : await SignInFlow.RunAsync(cancel.Token);
