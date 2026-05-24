using System.Collections.Generic;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// ユーザー登録のカラースキーマ JSON 群を読み込むためのソース抽象。
///
/// Application 層から <c>System.IO</c> / <c>Windows.Storage</c> を直接呼ばないために存在する。
/// Infrastructure 側で <see cref="DirectoryPath"/> の物理パス、<see cref="Load"/> の実 IO を担当する。
/// </summary>
public interface IColorSchemeSource
{
    /// <summary>テーマ JSON を配置するフォルダーの絶対パス (表示用)。</summary>
    string DirectoryPath { get; }

    /// <summary>
    /// 保存フォルダーが存在しなければ作成する。失敗時もスローしない (best-effort)。
    /// </summary>
    void EnsureDirectoryExists();

    /// <summary>
    /// フォルダー直下の <c>*.json</c> を読み込み、<c>(id, rawJsonText)</c> のリストを返す。
    /// 読み込めなかったファイルはスキップする。順序は ID で昇順 (case-insensitive)。
    /// 例外は出さない (best-effort)。
    /// </summary>
    IReadOnlyList<ColorSchemeJsonEntry> Load();
}

/// <summary>
/// <see cref="IColorSchemeSource.Load"/> が返す 1 件分のエントリ。
/// </summary>
/// <param name="Id">ファイル名から拡張子を除いたもの (例: <c>monokai-dimmed</c>)。</param>
/// <param name="JsonText">JSON ファイルの全文。</param>
public sealed record ColorSchemeJsonEntry(string Id, string JsonText);
