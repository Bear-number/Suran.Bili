using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.Bili;

/// <summary>B站查询与互动 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class BiliUI : ModuleUIBase<BiliModule, BiliConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "B站查询与互动", "视频详情、搜索、UP主、评论弹幕；登录后能读收藏夹与历史，写操作默认关且要确认", "#fb7299");
    }
}
