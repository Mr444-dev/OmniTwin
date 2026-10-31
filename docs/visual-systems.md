# Wizualne systemy OmniTwin — etap 6

Etap 6 dodaje nakładkę SAM zsynchronizowaną z obrazem PIP, wielowiązkowy LiDAR w symulacji, rozpoznawanie roli nowych stabilnych obiektów oraz jawne generowanie tekstury stołu. Integracje modelowe używają wyłącznie `META_API_KEY` w środowisku backendu. Unity nie przechowuje sekretu.

## PIP i pochodzenie masek

**SHOW CAMERA PREVIEW** pobiera z `/api/camera/annotated` klatkę JPEG oraz osobny, przezroczysty PNG dopasowany do jej szerokości i wysokości. Obie warstwy mają ten sam session ID, numer klatki i czas przechwycenia. Zapamiętana maska może być nałożona tylko wtedy, gdy lokalny optical flow zmierzył jej przesunięcie na dokładnie ten numer klatki. Zamknięty schemat kontroluje pochodzenie i świeżość przed dekodowaniem tekstur.

Kontur **ciągły** oznacza `SamVerified` z tej klatki; **przerywany** oznacza wcześniejszą maskę przesuniętą przez `LocalMotionEstimate`, z pokazanym wiekiem ostatniego SAM. Wypełnienie i obrys są cyjanowe dla zwykłego śladu i bursztynowe dla zagrożenia. Etykieta zawiera stabilny ID oraz confidence SAM. **HIDE SAM MASKS** ukrywa tylko nakładkę. Obraz i overlay wygasają po 750 ms; opóźniony SAM nie jest przypinany do nowszego obrazu.

Warstwa podglądu pozostaje osobna od wejścia nawigacyjnego. Przy wyłączonej kamerze albo braku świeżego obrazu HUD pokazuje niedostępność zamiast pustego pola interpretowanego jako wolny obszar.

## LiDAR agentów

Każdy z trzech AGV i dwóch dronów wykonuje lokalnie niealokujący skan raycastowy: **48 wiązek, 360°, do 6 m, 20 Hz**. Parametry są serializowane na komponencie `LidarSensor`. Odczyt raportuje liczbę wiązek, liczbę trafień, minimum w metrach (lub `null`, jeśli nie było trafień) oraz wiek próbki. HUD pokazuje minimum dla każdego agenta. Wiązki są schowane na starcie; **SHOW LIDAR BEAMS** włącza renderowanie wiązek i punktów uderzeń, nie wpływając na skan.

Lokalny bezpiecznik przed krokiem ruchu odrzuca ruch przy próbce starszej niż 250 ms, bliskim trafieniu w kierunku celu albo kolizji obrysu kapsuły z wykrytym colliderem. To pomiar i guard **symulacji Unity**, nie fizyczny lidar, nie gwarancja omijania dowolnych obiektów i nie certyfikat bezpieczeństwa robota. `NavMeshObstacle` wciąż realizuje osobną warstwę nawigacji, a czas carvingu i replanningu zależy od Unity.

## Semantyka nowych śladów

Nowy ślad musi zostać potwierdzony przez co najmniej **dwie kolejne zaakceptowane obserwacje SAM**, oddzielone co najmniej 500 ms, ze confidence co najmniej 0.60. Klient zleca analizę wyciętego, zamaskowanego ROI w ograniczonej kolejce w tle; publikuje się tylko role `obstacle`, `hazard`, `rescue_target`, `unknown`, skalę zagrożenia `none`, `caution`, `high`, `unknown`, krótką etykietę i confidence. Model **Muse Spark `muse-spark-1.3`** otrzymuje obraz inline w wiadomości użytkownika i wymuszony JSON Schema. Interfejs obrazu w Chat Completions obsługuje `image_url` i data URL; odpowiedź modelu jest ponownie walidowana po stronie serwera. [Meta image understanding](https://dev.meta.ai/docs/image-understanding), [Chat Completions schemas](https://dev.meta.ai/docs/api-reference/chat-completions/schemas), [structured output](https://dev.meta.ai/docs/cookbook/structured-output).

Wykrycie, geometria, pewność, obserwacja i wygasanie nadal pochodzą z SAM/trackerów. Klasyfikacja nie tworzy geometrii ani nie przedłuża świeżości. Etykiety nie są rozpoznaniem chemikaliów ani oceną rzeczywistego zagrożenia. Panel operatora umożliwia edycję etykiety/roli/tagu hazardu; ręczny tag ma pierwszeństwo do czasu jego wyczyszczenia. Pamięć jest ograniczona do sesji kamery; jej zmiana usuwa poprzednie tagi i ignoruje spóźnione odpowiedzi modelu.

## Muse Image i powierzchnia stołu

Operator jawnie uruchamia **GENERATE SCENERY** z pojedynczym opisem. Usługa wysyła jeden prompt do `/v1/images/generations`, model `muse-image-1.0`, `n=1`, Base64 i PNG, wyłącza image search, web search oraz shell. Meta dokumentuje te pola sterujące endpointu Images. [Muse Image generation](https://dev.meta.ai/docs/image-generation), [images request/response schemas](https://dev.meta.ai/docs/api-reference/images/schemas).

Backend sprawdza JSON, Base64, format, CRC PNG lub kontener JPEG, wymiary do 2048 px i limit rozmiaru przed dekoderem natywnym. Odpowiedź jest zmniejszana do maksymalnie 1024 px, obniżany jest jej kontrast, a Unity sprawdza raster ponownie przed `LoadImage`. Tekstura jest przypisywana do instancji materiału URP powierzchni; siatka taktyczna jest oddzielną sceną i pozostaje widoczna. **RESET SCENERY** natychmiast przywraca pierwotny materiał. Nieakceptowany URL, błędny obraz, timeout i błąd API pozostawiają albo przywracają materiał domyślny.

Backend wykonuje jedno generowanie naraz, przechowuje najwyżej 16 identyfikatorów żądań dla ograniczonej idempotencji oraz jedną ukończoną teksturę w pamięci. Restart serwera ją usuwa. Żądanie zaakceptowane przez backend może zakończyć się i zostać rozliczone, nawet jeśli operator przywróci domyślną powierzchnię w Unity; późna odpowiedź nie może ponownie aktywować lokalnej tekstury. Meta może rozliczać generowanie; uruchamiaj je tylko na żądanie operatora.

## Endpointy i diagnostyka

Podgląd oraz status są oddzielone od komend sterujących:

| Endpoint | Działanie |
| --- | --- |
| `GET /api/camera/annotated` | Atomowa para JPEG + PNG warstwy oraz frame/session/time/provenance |
| `GET /api/semantics/status` | Pamięć bieżącej sesji bez obrazów, promptów i surowych odpowiedzi |
| `POST /api/semantics/override` | Ręczny tag albo usunięcie tagu bieżącego stabilnego śladu |
| `POST /api/scenery/generate` | Jawne, idempotentne zlecenie tekstury |
| `GET /api/scenery/status/{id}` | Bezpieczny status konkretnego żądania |
| `GET /api/scenery/image/{id}` | Wyłącznie ostatni ukończony raster, jako JPEG |

Media API ogranicza się do loopback/same-origin, rozmiaru JSON i timeoutu uploadu. Błędy wyświetlane w Unity są krótkimi kategoriami; sekrety, obrazy i surowe ciała Meta nie trafiają do HUD ani logów adaptera.

## Konfiguracja i ograniczenia odbioru

Zainstaluj Unity adapter przez [instrukcję klienta](unity-client.md), uruchom lokalny backend i ustaw `META_API_KEY` w jego procesie zgodnie z [przewodnikiem kluczy](api-keys.md). `BASETEN_API_KEY` jest wymagany osobno tylko dla jawnej syntezy taktycznej. Gdy percepcja jest wyłączona, nie powstaje obraz PIP, maska ani klasyfikacja — brak źródła nie jest interpretowany jako brak przeszkód. Testy mogą sprawdzać protokół przez własny lokalny serwer fixture bez kontaktu z Meta; **nie potwierdzają** kredytów, praw dostępu, fizycznej kamery, rzeczywistej semantyki zdjęć, dokładności optical flow na Twoim stole ani wydajności sustained. Aktualne wyniki są w [raporcie weryfikacji](verification.md) i [raporcie odbioru](project-audit.md).
