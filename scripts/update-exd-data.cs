#:project ../src/Lodestone.Core/Lodestone.Core.csproj
#:property PublishAot=false

using Lodestone.Core.Assets;

var path = args.Length == 0 ? Path.Combine("data", "exd", "zh-cn.json.gz") : args[0];
try
{
    var catalog = await new ExdDataStore(path).RefreshAsync(Console.WriteLine);
    Console.WriteLine($"EXD {catalog.Version} saved to {Path.GetFullPath(path)}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
