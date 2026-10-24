# tools/ — 一次性数学校验（回归用）

这里放**验完即删、但源码留库**的独立校验程序。它们不进主程序集
（`Bianjing.csproj` 有 `<Compile Remove="tools/**/*.cs" />`），只在校验数学正确性时手工跑一次。

## 为什么值得留库

姿态数学、平面布局、音频合成有一个共同点：**编译通过 ≠ 结果正确**。

- `RigMathCheck.cs` 抓到 2 个「不报错、只是人物不对劲」的 FK bug（肩关节高度、袖长）；
- `LayoutMathCheck.cs` 抓到 4 类平面 bug（临路方向反了、索引出界、整带偏移）；
- `AudioMathCheck.cs` 抓到拨弦旋钮耦合（音符只响 16ms）、以及校验自身两次判错。

这些 bug 在实机上只表现为「看着怪 / 听着不对」，极难定位。改对应算法后重跑一遍，
是防止回归的唯一可靠手段。

## 怎么跑

以 `AudioMathCheck.cs` 为例（另两个同理，把名字换掉）：

```bash
mkdir -p tools/audiocheck && cd tools/audiocheck
cat > audiocheck.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../AudioMathCheck.cs" />
    <Reference Include="GodotSharp">
      <HintPath>D:\Godot\Bianjing\.godot\mono\temp\bin\Debug\GodotSharp.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
EOF
dotnet build audiocheck.csproj -nologo && dotnet run --no-build
cd .. && rm -rf audiocheck   # 验完即删
```

**注意**：校验程序必须与 `scripts/` 下的生产代码保持同构（同一份算法、同一个随机种子）。
改生产代码时同步改镜像，否则验的是旧逻辑。

**Godot 原生类型不能在引擎外构造**（`new AudioStreamWav()` 会 AccessViolation，
静态构造走原生 StringName）——所以音频校验是纯数学（只测 `float[]`），
WAV 字节打包用同公式单独验往返。
