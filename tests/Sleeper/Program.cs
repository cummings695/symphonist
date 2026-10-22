using System.Text;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

switch (args.Length > 0 ? args[0] : "")
{
    case "echo":
        var stdin = Console.In.ReadToEnd();
        Console.Out.Write(args[1] + "|" + stdin);
        return 0;
    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(60));
        return 0;
    case "flood":
        var chunk = new string('x', 1000);
        for (var i = 0; i < 2000; i++) { Console.Out.WriteLine(chunk); Console.Error.WriteLine(chunk); }
        return 0;
    case "env":
        Console.Out.Write(Environment.GetEnvironmentVariable(args[1]) ?? "<unset>");
        return 0;
    default:
        return 2;
}
