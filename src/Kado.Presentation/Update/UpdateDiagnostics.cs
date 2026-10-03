using System.Net;
using System.Net.Http;
using Kado.Core.Net;

namespace Kado.Presentation.Update;

/// <summary>
/// 更新の通信を、shell.log に1行で残せる形に整える。
/// <para>
/// 中身は <see cref="NetworkDiagnostics"/>（Google・実働日の配信と共通）に移した。
/// ここは更新の側から使い慣れた名前で呼べるようにしてあるだけ。
/// </para>
/// </summary>
public static class UpdateDiagnostics
{
    /// <inheritdoc cref="NetworkDiagnostics.MaxExceptionDepth"/>
    public const int MaxExceptionDepth = NetworkDiagnostics.MaxExceptionDepth;

    /// <inheritdoc cref="NetworkDiagnostics.Summarize"/>
    public static string Summarize(Exception? ex) => NetworkDiagnostics.Summarize(ex);

    /// <inheritdoc cref="NetworkDiagnostics.SafeUrl(Uri?)"/>
    public static string SafeUrl(Uri? uri) => NetworkDiagnostics.SafeUrl(uri);

    /// <inheritdoc cref="NetworkDiagnostics.SafeUrl(string?)"/>
    public static string SafeUrl(string? url) => NetworkDiagnostics.SafeUrl(url);

    /// <inheritdoc cref="NetworkDiagnostics.DescribeResponse"/>
    public static string DescribeResponse(HttpResponseMessage response) =>
        NetworkDiagnostics.DescribeResponse(response);

    /// <inheritdoc cref="NetworkDiagnostics.DescribeRedirect"/>
    public static string? DescribeRedirect(string requestedUrl, HttpResponseMessage response) =>
        NetworkDiagnostics.DescribeRedirect(requestedUrl, response);

    /// <inheritdoc cref="NetworkDiagnostics.DescribeProxy"/>
    public static string DescribeProxy(IWebProxy proxy, Uri target, bool usesDefaultCredentials = false) =>
        NetworkDiagnostics.DescribeProxy(proxy, target, usesDefaultCredentials);

    /// <inheritdoc cref="NetworkDiagnostics.LooksLikeExecutable"/>
    public static bool LooksLikeExecutable(ReadOnlySpan<byte> head) => NetworkDiagnostics.LooksLikeExecutable(head);

    /// <inheritdoc cref="NetworkDiagnostics.IsHtml"/>
    public static bool IsHtml(string? contentType) => NetworkDiagnostics.IsHtml(contentType);
}
