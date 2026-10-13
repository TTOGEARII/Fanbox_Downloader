Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$outDir = $args[0]
New-Item -ItemType Directory -Force $outDir | Out-Null
$frames = 24

for ($i = 0; $i -lt $frames; $i++) {
    $t = $i / $frames
    # 카오모지 둥실거림 (한 루프에 한 번 위아래)
    $bobY = [Math]::Round(-5 * [Math]::Sin(2 * [Math]::PI * $t), 2)
    # 반짝이 5개: 위상차를 둔 깜빡임
    $op = @()
    foreach ($ph in 0.0, 0.2, 0.45, 0.65, 0.85) {
        $op += [Math]::Round(0.15 + 0.85 * (0.5 + 0.5 * [Math]::Sin(2 * [Math]::PI * ($t + $ph))), 2)
    }
    # 진행바 marquee: 블록(폭 120)이 트랙(폭 444)을 왼→오로 통과
    $barX = [Math]::Round(-120 + 564 * $t, 1)

    $xaml = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      Width="480" Height="300" Background="#0E0E12">
  <Border Margin="18,18,18,46" CornerRadius="14" Background="#1C1B22"
          BorderBrush="#FF77A8" BorderThickness="1.5">
    <StackPanel VerticalAlignment="Center">
      <TextBlock Text="(ﾉ´ヮ&#x60;)ﾉ*:･ﾟ✧" FontSize="40" FontFamily="Segoe UI, Malgun Gothic"
                 HorizontalAlignment="Center" Foreground="#FFB7D5">
        <TextBlock.RenderTransform><TranslateTransform Y="$bobY"/></TextBlock.RenderTransform>
      </TextBlock>
      <TextBlock Text="새 모습으로 갈아입는 중이에요..." FontSize="16" FontFamily="Malgun Gothic"
                 HorizontalAlignment="Center" Margin="0,16,0,0" Foreground="#E6E6EB"/>
      <TextBlock Text="☆ 금방 돌아올게요, 잠시만 기다려 주세요 ☆" FontSize="12" FontFamily="Malgun Gothic"
                 HorizontalAlignment="Center" Margin="0,10,0,0" Foreground="#8A8A95"/>
      <TextBlock Text="Fanbox Updater" FontSize="11" FontFamily="Segoe UI"
                 HorizontalAlignment="Center" Margin="0,18,0,0" Foreground="#5A5A66"/>
    </StackPanel>
  </Border>
  <Canvas>
    <TextBlock Canvas.Left="70"  Canvas.Top="62"  Text="✧" FontSize="20" Foreground="#FF77A8" Opacity="$($op[0])"/>
    <TextBlock Canvas.Left="372" Canvas.Top="52"  Text="✦" FontSize="15" Foreground="#FFB7D5" Opacity="$($op[1])"/>
    <TextBlock Canvas.Left="96"  Canvas.Top="170" Text="✦" FontSize="13" Foreground="#FFB7D5" Opacity="$($op[2])"/>
    <TextBlock Canvas.Left="356" Canvas.Top="166" Text="✧" FontSize="18" Foreground="#FF77A8" Opacity="$($op[3])"/>
    <TextBlock Canvas.Left="236" Canvas.Top="34"  Text="☆" FontSize="13" Foreground="#FFB7D5" Opacity="$($op[4])"/>
  </Canvas>
  <Border Height="8" Margin="18,0,18,22" VerticalAlignment="Bottom"
          CornerRadius="4" Background="#1C1B22" ClipToBounds="True">
    <Canvas>
      <Border Canvas.Left="$barX" Width="120" Height="8" CornerRadius="4" Background="#FF77A8"/>
    </Canvas>
  </Border>
</Grid>
"@

    $element = [Windows.Markup.XamlReader]::Parse($xaml)
    $size = New-Object Windows.Size 480, 300
    $element.Measure($size)
    $element.Arrange((New-Object Windows.Rect 0, 0, 480, 300))
    $element.UpdateLayout()

    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap 480, 300, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($element)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $path = Join-Path $outDir ("frame{0:d2}.png" -f $i)
    $out = [IO.File]::Create($path)
    $enc.Save($out)
    $out.Close()
}
Write-Output "saved $frames frames to $outDir"
