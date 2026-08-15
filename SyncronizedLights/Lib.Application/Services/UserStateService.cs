using System.IO;
using System.Text.Json;
using Lib.Application.Models;
using Serilog;

namespace Lib.Application.Services
{
    /// <summary>
    /// ユーザー状態の永続化サービス
    /// 概要：%LocalAppData%\SynchronizedLights\user-state.json に保存・復元する。
    /// シングルトン的に静的利用を想定。
    /// </summary>
    public class UserStateService
    {
        /// <summary>
        /// 保存先ディレクトリ名
        /// </summary>
        private const string AppFolderName = "SynchronizedLights";

        /// <summary>
        /// 保存ファイル名
        /// </summary>
        private const string StateFileName = "user-state.json";

        /// <summary>
        /// 保存先フルパス
        /// </summary>
        public string FilePath { get; }

        public UserStateService() : this(null) { }

        public UserStateService(string? baseDir)
        {
            var appDataDir = !string.IsNullOrWhiteSpace(baseDir)
                ? baseDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
            Directory.CreateDirectory(appDataDir);
            FilePath = Path.Combine(appDataDir, StateFileName);
        }

        /// <summary>
        /// UserStateを読み込む。ファイルがなければ既定値を返す。
        /// </summary>
        public UserState Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Log.Information("UserState: ファイル未存在、既定値を返却 ({Path})", FilePath);
                    return new UserState();
                }

                var json = File.ReadAllText(FilePath);
                var state = JsonSerializer.Deserialize<UserState>(json);
                if (state == null)
                {
                    Log.Warning("UserState: 読み込み失敗（null）、既定値を返却");
                    return new UserState();
                }

                Log.Information("UserState: 読み込み完了 ({Path})", FilePath);
                return state;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UserState: 読み込み例外、既定値を返却");
                return new UserState();
            }
        }

        /// <summary>
        /// UserStateを保存する。失敗しても例外を投げない。
        /// </summary>
        public void Save(UserState state)
        {
            try
            {
                state.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(state, options);
                File.WriteAllText(FilePath, json);
                Log.Information("UserState: 保存完了 ({Path})", FilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "UserState: 保存失敗");
            }
        }
    }
}