# tools

## 스플래시 GIF 재생성

WPF 렌더링으로 프레임 24장을 뽑고 Pillow로 GIF를 조립한다 (80ms/프레임, 무한 루프).

```powershell
# PS 5.1은 BOM 없는 UTF-8을 ANSI로 읽으므로 BOM을 붙여 실행할 것
powershell -Sta -File tools\gen-splash-frames.ps1 <프레임출력폴더>        # 업데이트 적용용 (marquee 바 포함)
powershell -Sta -File tools\gen-splash-setup-frames.ps1 <프레임출력폴더>  # Setup.exe용 (바 없음 — Velopack 실제 진행바 자리)
```

```python
from PIL import Image; import glob
fs = sorted(glob.glob(r'<프레임폴더>/frame*.png'))
imgs = [Image.open(f).convert('RGB').quantize(colors=128, dither=Image.NONE) for f in fs]
imgs[0].save('splash.gif', save_all=True, append_images=imgs[1:], duration=80, loop=0, optimize=True)
```

- `assets/splash.gif` — 앱 내 업데이트 적용 스플래시 (WinForms PictureBox가 재생, 가짜 marquee 바가 GIF에 구워져 있음)
- `assets/splash-setup.gif` — Setup.exe 설치 스플래시 (바 없음, 하단 공간에 Velopack이 실제 진행바를 그림)
- 카오모지의 백틱은 PowerShell 이스케이프와 충돌하므로 XAML에서 `&#x60;` 엔티티 사용
