using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// ファイルシステムへの最小限の読み取り・存在確認の抽象。
/// Application 層から <c>System.IO.File</c> / <c>System.IO.Directory</c> を直接呼ばないために存在する。
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>
    /// 指定フォルダー直下のエントリ (ファイル + サブフォルダー) を列挙する。
    /// アクセス不能なフォルダーは空列挙を返し、例外は出さない (SPEC: quiet behavior)。
    /// </summary>
    IEnumerable<string> EnumerateFileSystemEntries(string folderPath);

    /// <summary>
    /// 指定パスがディレクトリかつ存在するかを返す。シンボリックリンク/ジャンクションも対象。
    /// </summary>
    bool IsDirectory(string path);

    /// <summary>
    /// 隠しまたはシステム属性を持つエントリかどうかを判定する。
    /// 例外時は <c>false</c> を返す (アクセス不能でも上位は走査を続行させる)。
    /// </summary>
    bool IsHiddenOrSystem(string path);

    /// <summary>
    /// 指定ファイルの最終更新日時 (UTC) を返す。更新日順の一覧表示で使う。
    /// 取得できない場合 (存在しない / アクセス不能) は <see cref="DateTimeOffset.MinValue"/> を返し、
    /// 例外は出さない (上位のソートでは末尾に沈む)。
    /// </summary>
    DateTimeOffset GetLastWriteTimeUtc(string path);
}
