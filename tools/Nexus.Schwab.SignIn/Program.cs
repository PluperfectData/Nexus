using Nexus.Schwab.SignIn;

using var cancel = new CancellationTokenSource(SignInFlow.Timeout);
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancel.Cancel();
};

return await SignInFlow.RunAsync(cancel.Token);
