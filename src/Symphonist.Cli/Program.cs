using Symphonist.Cli;
using Symphonist.Core;
using Symphonist.Integrations.Secrets;

var portText = Environment.GetEnvironmentVariable("SYMPHONIST_PORT");
var port = int.TryParse(portText, out var p) && p is > 0 and <= 65535 ? p : 5199;

// Read-only: a missing token file means the host never ran, which the client reports as "not running".
using var handler = new SocketsHttpHandler();
using var client = new SymphonistClient(handler, ApiToken.TryLoad(SymphonistPaths.Resolve("api-token")), port);

return await Cli.RunAsync(args, client, Console.Out, Console.Error, new CredentialManagerSecretStore(), Console.In,
    interactive: !Console.IsInputRedirected);
