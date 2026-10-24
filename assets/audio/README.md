# 音频资产目录（批次一百零三）

**当前状态：音频文件已全部入库**（18 个，约 19MB），放在本目录即生效，无需改任何代码。
文件由 Godot 编辑器导入（`.godot/imported/` 缓存 + 源文件旁的 `.import`）。
换素材时直接覆盖同名文件即可；`.import` 会随文件变化自动重新生成。

## 目录约定

```
assets/audio/
  music/     ← BGM（.mp3）
  sfx/       ← 交互音效（.wav）
  ambience/  ← 人群环境音（.ogg）：相机靠近人群时按密度播放
```

## BGM（music/）

| cue | 场景 | 文件 | 曲目 | 作者 |
|---|---|---|---|---|
| `market_day` | 白天·市井（默认起播） | `beijing_decade.mp3` | A Decade in Beijing (1:57, World) | Michael Ramir C. |
| `night_calm` | 夜间（黄昏/夜半自动切） | `night_meditation.mp3` | Meditation (1:58, Ambient) | Arulo |
| `morning_village` | 晨间 | `serene_morning.mp3` | Serene Moments (1:59, Chillout) | Ahjay Stelino |
| `celebration` | 晋级/庆典 | `new_year.mp3` | Happy Chinese New Year! (2:33, World) | Michael Ramir C. |

同一 cue 配多首时轮流播放，曲间 2 秒交叉淡变。

## 音效（sfx/）

全部取自 Mixkit **Interface / Chimes** 分类——选型标准是**柔和**（实机反馈早期的
程序化拨弦"太刺耳"）。文件名 = cue id，改 `scripts/configs/AudioConfig.cs` 的
`Sfx` 表即可换文件或加新 cue。

| 类别 | cue → 文件 | 原曲名 |
|---|---|---|
| UI | `ui_click` `ui_open` `ui_close` `ui_error` `ui_build` `ui_demolish` | Select click / Opening software interface / Software interface back / Click error / Software interface start / Software interface remove |
| 点人 | `pick_citizen` `pick_elder` `pick_child` | Page forward single chime / Page back chime / Page turn chime |
| 点物（按材质） | `pick_wood` `pick_stone` `pick_tile` `pick_goods` `pick_water` | Interface device click / Negative tone interface tap / Cool interface click tone / Interface option select / Water |
| 事件（无文件，走程序化兜底） | `news` `milestone` `tech` | — |

## 授权（Mixkit Free License）

以上全部来自 [Mixkit](https://mixkit.co/)（Envato 旗下免费素材站）：

- **Mixkit Free License**：可自由用于商业与非商业项目，**无需署名、无需注册**；
- 明确禁止的只有：转售素材本身、用于非法用途。用作游戏内音效/音乐完全在许可内。
- 条款全文：https://mixkit.co/license/

> 换用其他来源时注意：**CC-BY / OGA-BY 必须署名**（写进本表），
> **CC-BY-SA / GPL 有传染性不能用**，许可不明的站不碰。

## 人群环境音（ambience/）

相机焦点 **14m 半径内**的市民数驱动音量（满额 24 人，夜间 ×0.35），六首轮流播 +
0.8s 交叉淡变。选型标准是「人声嘈杂」而非「欢呼/掌声」——要市井底噪，不要事件音。
原曲为 WAV（共 90MB），已转 OGG Vorbis（44.1kHz 立体声，边缘 0.3s 淡变）。

| 文件 | 原曲 | 时长 |
|---|---|---|
| `crowd_city.ogg` | City downtown crowd ambience | 2:00 |
| `crowd_restaurant.ogg` | Restaurant crowd talking ambience | 2:00 |
| `crowd_talking.ogg` | Big crowd talking loop | 1:00 |
| `crowd_street.ogg` | Street ambience with walking people | 0:47 |
| `crowd_park.ogg` | Calm park with people and children | 1:37 |
| `crowd_fair.ogg` | People in fair ambience and laughter | 0:15 |

环境音走 **Sfx 总线**（随「音效音量」滑杆一起调节）。

## 没有文件时的兜底

`scripts/audio/ProceduralAudio.cs` 会现场合成古风音（五声音阶 + Karplus-Strong 拨弦）。
取流顺序：**文件 → 程序化合成 → 静默**，所以删掉这些文件游戏照样能跑（只是回到兜底音）。
设置页（ESC → 设置 → 音频）可看当前音频来源、可关兜底、可分别调三条总线的音量。
