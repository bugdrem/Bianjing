# 音频资产目录（批次一百零一）

把 **CC0**（或已确认可商用的）音频按下面的文件名放进对应子目录即生效，**无需改任何代码**。
未放置的文件静默不发声（不报错、不卡启动），可以只放 BGM 或只放音效。

## 目录约定

```
assets/audio/
  music/     ← BGM（.ogg / .wav / .mp3）
  sfx/       ← 交互音效
```

## BGM 文件名（`music/`）

| cue | 白天/场景 | 文件 |
|---|---|---|
| `market_day` | 白天·市井（默认起播） | `guzheng_morning.ogg` / `pipa_market.ogg` / `guzheng_street.ogg` |
| `night_calm` | 夜间（黄昏/夜半自动切） | `guqin_night.ogg` / `erhu_dusk.ogg` |
| `celebration` | 晋级/庆典 | `drum_celebrate.ogg` |

同一 cue 有多首时**轮流播放**，曲间 2 秒交叉淡变。

## 音效文件名（`sfx/`）

| 类别 | cue → 文件 |
|---|---|
| UI | `ui_click.ogg` `ui_open.ogg` `ui_close.ogg` `ui_error.ogg` `ui_build.ogg` `ui_demolish.ogg` |
| 点人 | `pick_citizen.ogg` `pick_elder.ogg`（低一档） `pick_child.ogg`（高一档） |
| 点物（按材质） | `pick_wood.ogg` `pick_stone.ogg` `pick_tile.ogg` `pick_goods.ogg` `pick_water.ogg` `pick_animal.ogg` `pick_tree.ogg` |
| 事件 | `news.ogg` `milestone.ogg` `tech.ogg` |

> 文件名可以改——改 `scripts/configs/AudioConfig.cs` 里的 `Tracks` / `Sfx` 两张表即可，
> 也可以加新 cue（表里加一行 + 调 `GameAudio.I?.Sfx("你的cue")`）。

## 推荐 CC0 来源

| 来源 | 许可 | 适合 | 搜索关键词（英文更准） |
|---|---|---|---|
| **Pixabay Music** | 全站 CC0，免署名 | **BGM 首选**，有 "China / Chinese Music" 分类 | `chinese music` `guzheng` `gong` `oriental` |
| **Freesound** | 逐件许可，**筛 CC0** | **音效首选** | `wood knock` `stone tap` `temple bell` `wood block` `water pour` |
| **OpenGameArt** | CC0 / CC-BY 套装 | 成套 UI 音 | `ui click` `rpg sfx pack` |

**明确不用**：CC-BY-SA / GPL（传染性许可会污染本项目）、许可不明的资源站。
CC-BY 可以用，但必须在游戏内或说明文件中署名——用前把作者与许可写进 `THIRD_PARTY.md`。

## 授权登记（用到的请补这里）

```
- <文件名> — <作者/来源> — <许可> — <URL>
```
