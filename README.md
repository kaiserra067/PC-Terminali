# PC Terminali

Windows için hepsi bir arada bilgisayar bakım paneli. Diskinizde **neyin ne kadar yer kapladığını** gösterir. **Program ve oyunları** kalıntılarıyla birlikte kaldırır, **gereksiz dosyaları** temizler. YouTube, YouTube Music, X (Twitter), TikTok ve Instagram'dan **video ve ses indirir**.

Arayüz Türkçedir; açık ve koyu temayı destekler.

## Özellikler

### Genel bakış
- Her sürücünün doluluğu. %85'in üzerinde uyarı, %95'in üzerinde kritik uyarı verir.
- Sürücüde en çok yer kaplayan klasörler ve dosya türlerine göre dağılım: video, oyun verisi, program, arşiv…
- Temizlenebilir alan, kalıntılar, oyunlar ve uygulamalar için özet kutuları.

### Disk analizi
- Bir sürücüyü ya da klasörü saniyeler içinde tarar; yaklaşık 900 bin dosya birkaç saniye sürer.
- Klasör klasör gezilebilen, sıralanabilir boyut listesi ve tıklanabilir **yer kaplama haritası** (treemap).
- Sağ tıkla Dosya Gezgini'nde gösterme, Geri Dönüşüm Kutusu'na taşıma ya da kalıcı silme.

### Uygulamalar
- Masaüstü programları ve Microsoft Store uygulamaları, kurulum klasöründen ölçülen **gerçek boyutlarıyla**.
- **Kaldır:** Programın kendi kaldırıcısını açar. Kaldırma bitince geride kalan klasörleri, kayıt defteri anahtarlarını ve kısayolları otomatik bulur.
- **Zorla kaldır:** Kaldırıcısı silinmiş, bozuk programları temizler.
- Oyun platformlarını (Steam, Epic…) kaldırırken, kütüphanedeki oyunların da etkileneceği konusunda uyarır.

### Oyunlar
- Steam, Epic Games, Riot Games, Battle.net, EA, Ubisoft, GOG, Xbox, Minecraft, CurseForge, Modrinth ve Prism Launcher oyunları, kapak görselleriyle.
- Her oyun kendi platformunun yöntemiyle kaldırılır. Örneğin Steam oyunu Steam'in kendi kaldırma penceresiyle kaldırılır; Epic oyununda klasörle birlikte Epic kayıtları da temizlenir.
- Atölye (Workshop) içeriği ve gölgelendirici önbelleği ayrıca temizlenebilir.
- Kaldırılmış oyunlardan Steam kütüphanesinde kalan sahipsiz klasörleri bulur.

### Gereksiz dosyalar
- Geçici dosyalar, Windows Update önbelleği, hata raporları ve bellek dökümleri.
- Ekran kartı gölgelendirici önbellekleri (NVIDIA, AMD, Intel, DirectX) ve sürücü kurulum artıkları.
- Tarayıcı önbellekleri: Edge, Chrome, Brave, Opera, Vivaldi, Yandex, Firefox. **Çerezlere, şifrelere ve geçmişe dokunulmaz.**
- Discord, Spotify, Teams, VS Code, Steam ve Epic önbellekleri; geliştirici paket önbellekleri.
- Windows'un kendi araçlarına kısayollar: WinSxS temizliği, hazırda bekletme dosyası, Disk Temizleme, Depolama Algısı.

### Kalıntılar
- Kaldırılmış programlardan kalan klasörler, artık kurulu olmayan Store uygulamalarının verileri, bozuk kısayollar ve kaldırıcısı silinmiş kayıtlar.
- Her öğenin yanında **güven düzeyi** (Kesin / Büyük olasılıkla / Emin değil) ve nedeni yazar.
- Kurulu bir programa, oyuna, hizmete ya da çalışan bir sürece ait klasörler asla listelenmez.

### Büyük ve yinelenen dosyalar
- Belirlenen boyutun üzerindeki dosyalar. Programlara ve oyunlara ait dosyalar kilitlidir, yanlışlıkla silinemez.
- İçeriği birebir aynı olan kopyalar: önce boyut, sonra dosyanın başı ve sonu, en son tam içerik karşılaştırılır. Her gruptan en az bir dosya her zaman korunur.

### Medya indirici
- **YouTube:** MP4, MKV, WEBM, MOV ya da AVI; 360p'den 4K'ya kalite seçimi; yalnızca ses.
- **YouTube Music:** Kendi biçimleri Opus ve M4A (yeniden kodlamadan, kayıpsız kopya) ya da MP3. Albüm ve çalma listesi desteklenir.
- **X (Twitter), TikTok, Instagram** (Reels, hikaye, gönderi) videoları.
- Kapak ve etiketleri dosyaya ekleme.
- Gizli içerik ve Instagram hikayeleri için tarayıcı oturumu (çerezler) kullanma.
- **Dönüştürme:**
  - Videodan GIF: kare hızı, genişlik, başlangıç ve süre seçilebilir.
  - Videodan sese: MP3, M4A, AAC, OGG, OPUS, FLAC, WAV.

### Ayrıca
- Tüm işlemlerin **işlem geçmişi**.
- Hiçbir zaman dokunulmayacak klasörler listesi.
- Yönetici olarak yeniden başlatma.

## Güvenlik

- Hiçbir silme işlemi Windows klasörlerine, sürücü köklerine ya da Belgeler, İndirilenler gibi ana kullanıcı klasörlerinin kendisine izin vermez. Bu kural tüm modüllerin geçtiği tek bir güvenlik katmanında uygulanır.
- Her silmeden önce neyin silineceğini ve ne kadar yer açılacağını gösteren bir onay penceresi çıkar.
- Kişisel dosyalar varsayılan olarak **Geri Dönüşüm Kutusu'na** taşınır.
- Kayıt defteri anahtarları silinmeden önce `.reg` dosyası olarak yedeklenir.
- Bağlantılar (junction/symlink) asla takip edilmez; kullanımdaki dosyalar atlanır.
- Uygulama normal kullanıcı olarak açılır. Windows klasörleri için yalnızca gerektiğinde yönetici izni ister.

## Kurulum

[Releases](../../releases) sayfasından birini indirin:

| Dosya | Açıklama |
|---|---|
| `PCTerminali.exe` | Küçük boyutlu (≈1 MB). [.NET 8 Masaüstü Çalışma Zamanı](https://dotnet.microsoft.com/download/dotnet/8.0) (ya da daha yenisi) gerekir. |
| `PCTerminali-tek-dosya.exe` | Hiçbir şey kurmadan çalışır; .NET içinde gelir. |

Kurulum gerektirmez: indirip çalıştırmanız yeterlidir. Windows SmartScreen uyarı verirse **Ek bilgi → Yine de çalıştır** seçeneğini kullanın; dosya imzalı değildir.

**Gereksinimler:** Windows 10 ya da 11 (64 bit).

## Kaynaktan derleme

[.NET SDK 8](https://dotnet.microsoft.com/download) ya da daha yenisi gerekir.

```powershell
dotnet build PCTerminali.csproj -c Release
# ya da derleyip bu kullanıcı için kurmak ve kısayol oluşturmak için:
powershell -ExecutionPolicy Bypass -File .\kur.ps1
```

Teknolojiler: C# · WPF · .NET 8. Harici NuGet paketi kullanılmaz.

## Veriler nerede?

Ayarlar, işlem geçmişi, kayıt defteri yedekleri ve medya araçları `%LOCALAPPDATA%\PCTerminali` klasöründedir. Uygulama internete yalnızca medya araçlarını indirmek ve medya indirmek için bağlanır; hiçbir veri toplamaz ya da göndermez.

## Üçüncü taraf araçlar

Medya indirici, aşağıdaki açık kaynaklı araçları **ilk kullanımda** resmi kaynaklarından indirir (toplam ≈280 MB). Bu araçlar bu depoda bulunmaz.

- [yt-dlp](https://github.com/yt-dlp/yt-dlp): video ve ses indirme
- [gallery-dl](https://github.com/mikf/gallery-dl): Instagram fotoğraf gönderileri
- [FFmpeg](https://ffmpeg.org) ([yt-dlp derlemesi](https://github.com/yt-dlp/FFmpeg-Builds)): birleştirme ve dönüştürme
- [Deno](https://deno.com): YouTube'un JavaScript doğrulamaları için

Siteler değiştikçe indirme bozulabilir. Bu durumda uygulamadaki **Araçları güncelle** düğmesini kullanın.

## Sorumluluk

- Temizlik ve kaldırma işlemleri dikkatle tasarlanmıştır, ama silme işlemleri doğası gereği risklidir. Onay pencerelerini okuyun; "Emin değil" olarak işaretlenen kalıntıları silmeden önce kontrol edin.
- Medya indirici yalnızca hakkına sahip olduğunuz ya da kişisel kullanım için indirilmesine izin verilen içerikler içindir. Platform kurallarına ve telif haklarına uymak kullanıcının sorumluluğundadır.
