using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lib.Application.Models;
using Serilog;

namespace Lib.Application.Services
{
    /// <summary>
    /// カスタム色プリセット（Custom 1〜4）の永続化サービス
    /// 概要：%LocalAppData%\SynchronizedLights\custom-colors.json に保存。
    ///       アプリ起動時に読み込み、編集時に保存。
    /// </summary>
    public class CustomColorStore
    {
        /// <summary>保存ファイルのフルパス</summary>
        private readonly string _filePath;

        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>カスタム色の既定件数</summary>
        public const int DefaultCount = 8;

        public CustomColorStore() : this(null) { }

        public CustomColorStore(string? baseDir)
        {
            var dir = !string.IsNullOrWhiteSpace(baseDir)
                ? baseDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynchronizedLights");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CustomColorStore] ディレクトリ作成失敗: {Dir}", dir);
            }
            _filePath = Path.Combine(dir, "custom-colors.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
        }

        /// <summary>
        /// カスタム色一覧を読み込む。ファイルが無ければ既定値を返す。
        /// </summary>
        public List<CustomColorEntry> LoadAll()
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaults();
            }
            try
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<CustomColorEntry>>(json, _jsonOptions);
                if (list == null || list.Count == 0)
                {
                    return CreateDefaults();
                }
                // 既定件数に合わせて補完または切り詰め
                while (list.Count < DefaultCount)
                {
                    list.Add(new CustomColorEntry { Name = $"Custom {list.Count + 1}", R = 255, G = 255, B = 255 });
                }
                if (list.Count > DefaultCount)
                {
                    list = list.GetRange(0, DefaultCount);
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CustomColorStore] 読込失敗、既定値を返します");
                return CreateDefaults();
            }
        }

        /// <summary>
        /// カスタム色一覧を保存する
        /// </summary>
        public bool SaveAll(IReadOnlyList<CustomColorEntry> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, _jsonOptions);
                File.WriteAllText(_filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CustomColorStore] 保存失敗: {Path}", _filePath);
                return false;
            }
        }

        /// <summary>
        /// 既定のカスタム色 4 件（現場でよく使う中間色）
        /// </summary>
        private static List<CustomColorEntry> CreateDefaults() => new()
        {
            new() { Name = "Custom 1", R = 255, G = 128, B =  64 },   // 暖橙
            new() { Name = "Custom 2", R = 128, G = 255, B = 128 },   // 淡緑
            new() { Name = "Custom 3", R = 128, G = 192, B = 255 },   // 淡青
            new() { Name = "Custom 4", R = 255, G = 220, B = 180 },   // 暖白
            new() { Name = "Custom 5", R = 255, G = 255, B =   0 },   // イエロー
            new() { Name = "Custom 6", R = 255, G =   0, B = 255 },   // マゼンタ
            new() { Name = "Custom 7", R =   0, G = 255, B = 255 },   // シアン
            new() { Name = "Custom 8", R = 255, G = 160, B = 200 },   // ピンク
        };
    }
}
