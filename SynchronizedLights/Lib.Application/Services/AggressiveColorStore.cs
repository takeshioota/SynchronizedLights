using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lib.Application.Models;
using Serilog;

namespace Lib.Application.Services
{
    /// <summary>
    /// 煽りボタン用カラープリセットの永続化サービス
    /// 概要：%LocalAppData%\SynchronizedLights\aggressive-colors.json に保存。
    ///       アプリ起動時に読み込み、編集時に保存。
    /// </summary>
    public class AggressiveColorStore
    {
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>煽りボタンの既定件数</summary>
        public const int DefaultCount = 2;

        public AggressiveColorStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "SynchronizedLights");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AggressiveColorStore] ディレクトリ作成失敗: {Dir}", dir);
            }
            _filePath = Path.Combine(dir, "aggressive-colors.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
        }

        /// <summary>
        /// 煽り色一覧を読み込む。ファイルが無ければ既定値を返す。
        /// </summary>
        public List<AggressiveColorEntry> LoadAll()
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaults();
            }
            try
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<AggressiveColorEntry>>(json, _jsonOptions);
                if (list == null || list.Count == 0)
                {
                    return CreateDefaults();
                }
                while (list.Count < DefaultCount)
                {
                    list.Add(new AggressiveColorEntry { Name = $"煽り {list.Count + 1}", R = 255, G = 255, B = 255 });
                }
                if (list.Count > DefaultCount)
                {
                    list = list.GetRange(0, DefaultCount);
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AggressiveColorStore] 読込失敗、既定値を返します");
                return CreateDefaults();
            }
        }

        /// <summary>
        /// 煽り色一覧を保存する
        /// </summary>
        public bool SaveAll(IReadOnlyList<AggressiveColorEntry> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, _jsonOptions);
                File.WriteAllText(_filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AggressiveColorStore] 保存失敗: {Path}", _filePath);
                return false;
            }
        }

        /// <summary>
        /// 既定の煽り色 2 件（ライブ演出で映える色味）
        /// </summary>
        private static List<AggressiveColorEntry> CreateDefaults() => new()
        {
            new() { Name = "煽り 1", R = 255, G = 255, B = 255 },  // 白（フラッシュ風）
            new() { Name = "煽り 2", R = 255, G =   0, B = 128 },  // ピンク（DJ ライブ風）
        };
    }
}
