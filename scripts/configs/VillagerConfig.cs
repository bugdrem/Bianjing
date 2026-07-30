namespace Bianjing;

/// <summary>
/// 村民表现层配置：模型缩放（业务归属：CitizenAgent 渲染，不影响数据层模拟）。
/// </summary>
public static class VillagerConfig
{
    /// <summary>成年人模型整体缩放（1.0 为原始大小；儿童在此基础上再按年龄折算）。</summary>
    public const float ModelScale = 0.25f;

    /// <summary>新生儿体型占成人的比例（体型从此值线性生长到成年门槛处的 1.0）。</summary>
    public const float ChildMinScale = 0.4f;
}
