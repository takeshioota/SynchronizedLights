using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lib.Application.Models;
using Serilog;

namespace Lib.Application.Services
{
    /// <summary>
    /// SNO端末の内蔵プログラム（A1コマンド）プリセットの永続化サービス
    /// 概要：%LocalAppData%\SynchronizedLights\internal-programs.json に保存。
    ///       アプリ起動時に読み込み、編集時に保存。プログラム番号は実機確認後に変更可能。
    /// </summary>
    public class InternalProgramStore
    {
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;

        public InternalProgramStore() : this(null) { }

        public InternalProgramStore(string? baseDir)
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
                Log.Warning(ex, "[InternalProgramStore] ディレクトリ作成失敗: {Dir}", dir);
            }
            _filePath = Path.Combine(dir, "internal-programs.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
        }

        /// <summary>
        /// 内蔵プログラム一覧を読み込む。ファイルが無ければ既定値を返す。
        /// </summary>
        public List<InternalProgramEntry> LoadAll()
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaults();
            }
            try
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<InternalProgramEntry>>(json, _jsonOptions);
                if (list == null || list.Count == 0)
                {
                    return CreateDefaults();
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InternalProgramStore] 読込失敗、既定値を返します");
                return CreateDefaults();
            }
        }

        /// <summary>
        /// 内蔵プログラム一覧を保存する
        /// </summary>
        public bool SaveAll(IReadOnlyList<InternalProgramEntry> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, _jsonOptions);
                File.WriteAllText(_filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[InternalProgramStore] 保存失敗: {Path}", _filePath);
                return false;
            }
        }

        /// <summary>
        /// 既定の内蔵プログラム 6 件（実機確認後に番号を変更可能）
        /// </summary>
        private static List<InternalProgramEntry> CreateDefaults() => new()
        {
            new() { Name = "Rainbow",  FrameNo = 0, Enabled = true },
            new() { Name = "満点星",   FrameNo = 1, Enabled = true },
            new() { Name = "Prog 2",   FrameNo = 2, Enabled = true },
            new() { Name = "Prog 3",   FrameNo = 3, Enabled = true },
            new() { Name = "Prog 4",   FrameNo = 4, Enabled = true },
            new() { Name = "Prog 5",   FrameNo = 5, Enabled = true },
        };
    }
}
