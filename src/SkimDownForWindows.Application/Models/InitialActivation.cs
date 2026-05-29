namespace SkimDownForWindows.Application.Models;

/// <summary>
/// アプリ起動 / 2 回目以降のアクティベーション (Explorer ダブルクリックなど) で
/// 「何を開くべきか」を表す discriminated record。
/// CLI 引数解析 (<see cref="CommandLine.CommandLineLauncher"/>) と File activation の両方で共通に使う。
/// </summary>
public abstract record InitialActivation;

/// <summary>
/// 指定フォルダーをツリービュー (通常モード) で開く。
/// </summary>
public sealed record OpenFolderActivation(string FolderPath) : InitialActivation;

/// <summary>
/// 指定 Markdown ファイルを single-file mode で開く。
/// 上流 macOS 版の挙動: サイドバー非表示・ツリー無し・そのファイル 1 つだけ表示・
/// RecentFolders / LastFolderPath / FolderState を更新しない。
/// </summary>
public sealed record OpenSingleFileActivation(string FilePath) : InitialActivation;
