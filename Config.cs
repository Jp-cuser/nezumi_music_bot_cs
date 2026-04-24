using System;
using System.Collections.Generic;
using System.Linq;
using DotNetEnv;

namespace NezumiMusicBot;

public class Config
{
    public string MainToken { get; set; } = string.Empty;
    public List<string> WorkerTokens { get; set; } = new();
    public string LavalinkUrl { get; set; } = "localhost:2333";
    public string LavalinkPass { get; set; } = "youshallnotpass";
    public List<ulong> AllowedGuilds { get; set; } = new() 
    { 
        1450709451488100396, 
        1483795902610145463 
    };

    public static Config Load()
    {
        // Load .env from the parent directory (original bot location) or current directory
        if (System.IO.File.Exists("../nezumi_music_bot/.env"))
        {
            Env.Load("../nezumi_music_bot/.env");
        }
        else
        {
            Env.Load();
        }

        var config = new Config
        {
            MainToken = Env.GetString("MAIN_TOKEN"),
            WorkerTokens = new List<string>()
        };

        for (int i = 1; i <= 9; i++)
        {
            var token = Env.GetString($"WORKER_{i}");
            if (!string.IsNullOrEmpty(token))
            {
                config.WorkerTokens.Add(token);
            }
        }

        return config;
    }
}
