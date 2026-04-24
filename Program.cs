using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NezumiMusicBot;

class Program
{
    static async Task Main(string[] args)
    {
        var config = Config.Load();

        var services = new ServiceCollection();
        services.AddLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(config);
        services.AddSingleton<BotManager>();

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var manager = serviceProvider.GetRequiredService<BotManager>();

        Console.WriteLine("🚀 Botの起動を開始します...");
        
        try
        {
            await manager.InitializeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 起動中にエラーが発生しました: {ex.Message}");
            return;
        }

        Console.WriteLine("✅ 起動完了。CTRL+Cで終了します。");
        await Task.Delay(-1);
    }
}
