using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lib.Application.Models;
using Serilog;

namespace Lib.Application.Services
{
    /// <summary>
    /// 時間ベースシーケンスの永続化サービス
    ///       %LocalAppData%\SynchronizedLights\time-sequences\&lt;Id&gt;.json として 1 シーケンス 1 ファイルで保存。
    ///       一覧取得・追加・更新・削除をサポート。
    ///
    /// ファイル配置例：
    ///   C:\Users\&lt;USER&gt;\AppData\Local\SynchronizedLights\time-sequences\
    ///     ├ a1b2c3d4...json   ("オープニング")
    ///     ├ e5f6a7b8...json   ("アンコール")
    ///     └ c9d0e1f2...json   ("ラスト")
    /// </summary>
    public class TimeBasedSequenceStore
    {
        /// <summary>
        /// 保存ディレクトリのパス
        /// %LocalAppData%\SynchronizedLights\time-sequences\
        /// </summary>
        private readonly string _storeDir;

        private readonly JsonSerializerOptions _jsonOptions;

        public TimeBasedSequenceStore() : this(null) { }

        public TimeBasedSequenceStore(string? baseDir)
        {
            var rootDir = !string.IsNullOrWhiteSpace(baseDir)
                ? baseDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynchronizedLights");
            _storeDir = Path.Combine(rootDir, "time-sequences");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            EnsureDirectory();
        }

        /// <summary>
        /// 保存ディレクトリの存在を保証する（無ければ作成）
        /// </summary>
        private void EnsureDirectory()
        {
            try
            {
                if (!Directory.Exists(_storeDir))
                {
                    Directory.CreateDirectory(_storeDir);
                    Log.Information("[TimeSeqStore] Created directory: {Dir}", _storeDir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TimeSeqStore] EnsureDirectory failed");
            }
        }

        /// <summary>
        /// 保存ディレクトリの絶対パスを取得（UI表示用）
        /// </summary>
        public string GetStoreDirectoryPath() => _storeDir;

        /// <summary>
        /// 全シーケンスを読み込む
        /// 概要：ディレクトリ内の *.json を全てパース。壊れたファイルはスキップ。
        ///       戻り値は名前順ソート。
        /// </summary>
        public List<TimeBasedSequence> LoadAll()
        {
            EnsureDirectory();
            var result = new List<TimeBasedSequence>();

            try
            {
                var files = Directory.GetFiles(_storeDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var seq = JsonSerializer.Deserialize<TimeBasedSequence>(json, _jsonOptions);
                        if (seq != null)
                        {
                            result.Add(seq);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[TimeSeqStore] Failed to load: {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TimeSeqStore] LoadAll failed");
            }

            return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 1 件読み込み
        /// </summary>
        public TimeBasedSequence? Load(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var path = GetFilePath(id);
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<TimeBasedSequence>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TimeSeqStore] Load failed: id={Id}", id);
                return null;
            }
        }

        /// <summary>
        /// シーケンスを保存（新規 or 更新）
        /// 概要：UpdatedAt を現在時刻に更新してから書き込む。
        /// </summary>
        public bool Save(TimeBasedSequence sequence)
        {
            if (sequence == null) return false;
            if (string.IsNullOrWhiteSpace(sequence.Id))
            {
                sequence.Id = Guid.NewGuid().ToString("N");
            }

            sequence.UpdatedAt = DateTime.Now.ToString("o");

            EnsureDirectory();
            var path = GetFilePath(sequence.Id);

            try
            {
                var json = JsonSerializer.Serialize(sequence, _jsonOptions);
                File.WriteAllText(path, json);
                Log.Information("[TimeSeqStore] Saved: {Name} (id={Id}, steps={Steps})",
                                sequence.Name, sequence.Id, sequence.Steps.Count);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TimeSeqStore] Save failed: id={Id}", sequence.Id);
                return false;
            }
        }

        /// <summary>
        /// シーケンスを削除
        /// </summary>
        public bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var path = GetFilePath(id);

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Information("[TimeSeqStore] Deleted: id={Id}", id);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TimeSeqStore] Delete failed: id={Id}", id);
                return false;
            }
        }

        /// <summary>
        /// 同じ名前のシーケンスが既に存在するか
        /// </summary>
        public bool ExistsByName(string name, string? excludeId = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return LoadAll().Any(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (excludeId == null || !string.Equals(s.Id, excludeId, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// シーケンスを名前で読み込む（Preset 実行時に使用）
        /// </summary>
        public TimeBasedSequence? LoadByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return LoadAll().FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// JSON ファイルパスを構築
        /// </summary>
        private string GetFilePath(string id)
        {
            // ファイル名に使えない文字を除去（GUID形式なら問題ないが念のため）
            var safe = string.Concat(id.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            return Path.Combine(_storeDir, $"{safe}.json");
        }
    }
}
