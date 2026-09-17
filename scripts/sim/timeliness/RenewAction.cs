namespace Bianjing;

/// <summary>
/// 恢复动作（翻新）的执行入口：回锅、烘干、重腌、晾晒……
///
/// <b>核心语义不是"回血"，而是用总寿命换当下品质</b>：动作把鲜度拉回一个固定值，
/// 但 <c>RenewCount</c> +1 会让寿限 ×0.65^n、衰减速率 ×1.8^n 复合叠加。
/// 于是递减收益自然涌现——第二次回锅时鲜度本就没掉多少，拉到同一个值等于白费一次折扣，
/// 玩家/NPC 自然会挑时机，不需要任何额外规则去禁止无限回锅。
///
/// 与"硬轴归零降级为废料"（<see cref="TimelinessSystem"/> 的 ConvertToScrap）的分工：
///   翻新 = 主动出手，赌它还能撑一阵；
///   降级 = 已失效后回收残值。
///
/// 两种消费者共用同一套动作表：
///   · 玩家：面板上的 <c>[url=renew:货品id]</c> 链接（见 <c>InspectPanel.OnMetaClicked</c>）；
///   · NPC：<see cref="TimelinessSystem"/> 的 AutoRenew 每日替居民打理家务。
/// </summary>
public static class RenewAction
{
    /// <summary>取某货品的恢复手段；未登记则返回 false（没有手段的就是没有，如铁器锈了只能认）。</summary>
    public static bool SpecOf(string goodsId, out RenewSpec spec)
        => TimelinessConfig.RenewSpecs.TryGetValue(goodsId, out spec);

    /// <summary>该货品是否有恢复手段（面板据此决定是否显示操作链接）。</summary>
    public static bool CanRenewGoods(string goodsId)
        => TimelinessConfig.RenewSpecs.ContainsKey(goodsId);

    /// <summary>
    /// 对库存中<b>最差的一批</b>该货品执行恢复动作（一次处理一整批，辅料按份数折算）。
    /// 返回是否成功；<paramref name="message"/> 成功与失败都会填写，供面板与新闻提示。
    /// </summary>
    public static bool Apply(Inventory inv, string goodsId, out string message)
    {
        message = "";
        if (!SpecOf(goodsId, out var spec))
        {
            message = $"{Goods.NameOf(goodsId)}没有可行的恢复手段";
            return false;
        }

        var batch = inv.WorstStackOf(goodsId);
        if (batch == null || batch.Amount <= 0.0001)
        {
            message = "没有可处理的存货";
            return false;
        }
        if (batch.Stage == FreshStage.Full)
        {
            message = $"{Goods.NameOf(goodsId)}尚在最佳状态，不必{spec.Verb}";
            return false;
        }
        if (batch.RenewCount >= TimelinessConfig.RenewMaxCount)
        {
            message = $"{Goods.NameOf(goodsId)}已处理过 {batch.RenewCount} 次，再折腾只会更糟";
            return false;
        }

        // 辅料检查（先查后扣，避免扣了又退的麻烦）
        double fuel = spec.NeedsFuel ? spec.FuelPerUnit * batch.Amount : 0;
        if (fuel > 0 && inv.AmountOf(spec.FuelGoodsId) < fuel - 0.0001)
        {
            message = $"辅料不足：需 {Goods.NameOf(spec.FuelGoodsId)} {fuel:F1} 份";
            return false;
        }

        // 执行：摘出该批 → 改状态 → 按新档位重新归档
        // （必须重新归档：鲜度与翻新次数都变了，<see cref="TimedRules.SameBand"/> 的档位随之改变，
        //   直接改字段会让它赖在错误的档里，后续合并与显示都会失真）
        double amount = batch.Amount;
        var st = batch.State;
        inv.Stacks.Remove(batch);
        if (fuel > 0)
            inv.Take(spec.FuelGoodsId, fuel); // 辅料自身也按最差优先取用
        // 恢复 = 把累积龄期回退到"目标鲜度"对应的位置（只减不增：已比目标更新就不动）
        float restored = System.MathF.Max(st.Fresh, spec.RestoreTo);
        st.FreshAgeDays = System.MathF.Min(st.FreshAgeDays,
            TimedRules.AgeForFresh(restored, TimedRules.BaselineOf(goodsId)));
        st.Fresh = restored;
        st.RenewCount++;
        inv.StoreForceBatch(goodsId, amount, st);

        message = $"{spec.Verb}完成：{Goods.NameOf(goodsId)} {amount:F1} 份鲜度回到 {st.Fresh:F0}%"
            + $"（第 {st.RenewCount} 次处理，此后衰减更快、保质期更短）";
        return true;
    }

    /// <summary>
    /// 处置<b>已失效</b>的存货：按比例降级为废料（<see cref="Goods.Scrap"/>，燃料类货品，可当柴烧）。
    /// 这就是"失效后可作他用"的落点——不是丢弃，而是换成仍有用处的次级物资。
    ///
    /// 硬轴归零时系统会自动做同样的事（<c>ConvertToScrap</c>）；
    /// 本方法是让玩家提前出手，趁早把仓容腾出来。
    /// </summary>
    public static bool Salvage(Inventory inv, string goodsId, out string message)
    {
        message = "";
        var batch = inv.WorstStackOf(goodsId);
        if (batch == null || batch.Amount <= 0.0001)
        {
            message = "没有可处置的存货";
            return false;
        }
        if (batch.Stage != FreshStage.Spent)
        {
            message = $"{Goods.NameOf(goodsId)}尚未失效，还可照常使用";
            return false;
        }

        double amount = batch.Amount;
        double keep = amount * TimelinessConfig.ExpiryScrapRatio;
        inv.Stacks.Remove(batch);
        if (keep > 0)
            inv.StoreForce(Goods.Scrap, keep); // 废料天然并堆，无需带状态

        message = $"已处置失效的{Goods.NameOf(goodsId)} {amount:F1} 份，得废料 {keep:F1} 份";
        return true;
    }

    /// <summary>
    /// 该库存中是否存在"值得出手"的批次：有恢复手段、已进入渐变衰减段、且未用尽翻新次数。
    /// NPC 自动打理与面板提示共用此判据。
    /// </summary>
    public static bool WorthRenewing(Inventory inv, string goodsId, out GoodsStack batch)
    {
        batch = inv.WorstStackOf(goodsId);
        if (batch == null || batch.Amount <= 0.0001)
            return false;
        if (!CanRenewGoods(goodsId))
            return false;
        if (batch.Fresh > TimelinessConfig.AutoRenewBelowFresh)
            return false; // 还没坏到值得动手的地步
        if (batch.RenewCount >= TimelinessConfig.AutoRenewMaxCount)
            return false; // 居民不会不计后果地反复翻新，把寿限留给真正需要的时候
        return true;
    }

    /// <summary>该库存中是否存在已失效（可处置回收）的批次。</summary>
    public static bool HasSpentBatch(Inventory inv, string goodsId, out GoodsStack batch)
    {
        batch = inv.WorstStackOf(goodsId);
        return batch != null && batch.Amount > 0.0001 && batch.Stage == FreshStage.Spent;
    }
}
