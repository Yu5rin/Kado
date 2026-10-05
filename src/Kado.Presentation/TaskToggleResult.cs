namespace Kado.Presentation;

/// <summary>タスクの完了を切り替えた結果。</summary>
/// <param name="IsDone">切り替えたあとの姿。完了になったなら true。</param>
/// <param name="NextDue">
/// 繰り返し付きのタスクを完了にして、次の回を作ったときの、その期限。作らなかったなら null。
/// </param>
public sealed record TaskToggleResult(bool IsDone, DateOnly? NextDue);
