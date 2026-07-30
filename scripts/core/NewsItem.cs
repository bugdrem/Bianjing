namespace Bianjing;

/// <summary>全城公告条目（公告栏展示用）：迁入迁出、出生离世等大事的一条播报。
/// Kind 为类别标签（migrate-in / migrate-out / birth / death …），供后续按类过滤、配图标等拓展。</summary>
public class NewsItem
{
    /// <summary>公告发生的游戏年月。</summary>
    public int Year;
    public int Month;

    /// <summary>类别标签（可拓展：灾害、开业、晋级等新类别直接加新标签即可）。</summary>
    public string Kind = "";

    /// <summary>公告正文（中文短句，直接用于公告栏展示）。</summary>
    public string Text = "";
}
