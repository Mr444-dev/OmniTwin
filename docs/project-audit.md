# Audyt stanu OmniTwin — 30 października 2026

Podstawa: implementacja z commitu `4130323`, konstytucja projektu, przegląd struktury i wybranych ścieżek wykonania, wyniki testów oraz dokumentacja dostawców. **Cały produkt z pierwotnej specyfikacji nie jest ukończony.** Gotowy jest fundament percepcji/transportu i scena prezentacyjna; sterowanie rojem i Brain wymagają implementacji.

„Zaimplementowane” oznacza obecność kodu z opisanymi sprawdzeniami. Testy syntetyczne i połączenie Unity z wyłączoną percepcją nie potwierdzają pracy całego systemu z kamerą i API. Nie podajemy procentu ukończenia bez zważonych kryteriów odbioru.

## Pokrycie specyfikacji

| Obszar | Rzeczywisty stan | Co pozostaje |
| --- | --- | --- |
| Kontrakty | `OmniTwin.Contracts`: niemutowalne, walidowane DTO i interfejs polityki; .NET 10 i .NET Standard 2.1; testy obu bibliotek | Połączenie agentów, misji i dyrektyw z rzeczywistym wykonaniem; same DTO nie sterują rojem |
| Kalibracja | Homografia, walidacja obszaru, `snapshot`/`calibration-check`, `/calibration`, walidowany eksport; pobranie sprawdzone w przeglądarce | Własny profil fizycznego stołu i pomiar błędu na niezależnych punktach |
| Kamera | Odczyt lokalnego urządzenia według indeksu OpenCV w tle, ostatnia klatka, JPEG, detekcja zmian; zapis zdjęć bez nadpisywania | Próba sprzętu, ekspozycji, kadru, rozdzielczości i utraty urządzenia; brak bezpośredniego wejścia URL kamery IP. Natywnego odczytu nie można siłowo anulować |
| SAM 3.1 | Prawdziwy adapter Responses API, parser `one_bit`, retry, timeout, limity i odrzucanie starych wyników; testy protokołu | Uwierzytelnione żądanie na koncie użytkownika i fizyczne obiekty |
| Pozycje/ID | Projekcja dolnego pasma maski, promień z marginesem, kojarzenie obserwacji, ID w sesji, wygasanie i pewność | Pomiar błędu kontaktu/rozmiaru, zasłonięcia i zamiany ID |
| Hybrid Vision | Lokalny frame-diff wybiera klatki do SAM; predykcja pomaga kojarzyć detekcje | Brak lokalnych konturów/HSV/optical flow aktualizujących X/Z podczas oczekiwania na SAM. Publikowane pozycje pozostają ostatnio zaobserwowane |
| Backend/WS | Loopback, status/health, walidowany strumień, ograniczone kolejki, reconnect i świeżość; rzeczywiste testy HTTP/WS | Zmierzenie kamera → SAM → Unity. Nominalne 30 Hz publikacji nie oznacza 30 nowych detekcji/s |
| Unity | 6000.6.3f1 URP, `TacticalDesk`, podłoga z NavMesh, siatka, kamera, HUD i wizualizator przeszkód; sprawdzono Play i prawdziwe połączenie z wyłączonym źródłem | Aktualne detekcje, kolizje i replanning z poruszającymi się przeszkodami |
| Roboty/drony/LiDAR | Kontrakty telemetrii i celów | Brak `SwarmAgentController`, działającego roju, sensorów, wykonania misji i telemetrii agentów |
| Baseten / Muse Spark Open | Zachowany wymagany identyfikator i opis rozbieżności z dokumentacją | Brak projektu `OmniTwin.Brain`, klienta Baseten i prawdziwego żądania. `BASETEN_API_KEY` nie jest konsumowany |
| Roslyn/samonaprawa | Opisana wymagana granica bezpieczeństwa; nie uruchamiamy nieufnego kodu w backendzie/Unity | Kompilator, analiza symboli/AST, diagnostyka, maks. trzy próby, izolowany wykonawca z limitami i walidacja dyrektyw |
| Polityki awaryjne | Brak trzech implementacji z pierwotnego planu | Patrol, kwarantanna, poszukiwania, bezpieczne zatrzymanie i wybór fallbacku po błędzie Brain |
| Głos/komendy | Brak ścieżki komend | Nagranie, Muse Voice/STT, tekstowy fallback, przyciski poleceń i planowanie |
| Semantyka | Etykieta pochodzi z pojęcia SAM; hazard ustawia operator; wysokość proxy jest parametrem | Klasyfikator multimodalny, pamięć rozpoznanych obiektów i mapowanie klas na wygląd. Hazard nie jest rozpoznaniem chemicznym |
| Scenerie/obrazy | Autorska scena URP | Brak klienta Muse Image i podmiany tekstur/scenerii. Muse Wonder nie jest zamiennikiem modelu obrazowego |
| Pełny HUD | Połączenie/świeżość, liczniki, wiek, ID kalibracji i reconnect | Kamera PIP, ścieżki/telemetria agentów, polecenia, decyzje Brain i logi Roslyn |
| Testy/wydanie | 291 lokalnych testów .NET, 12 sprawdzeń przeglądarkowych; prywatny GitHub, zsynchronizowany `main` | CI blokowane rozliczeniami konta; brak zweryfikowanego standalone/IL2CPP, długiego testu, benchmarku i pełnego nagrania demo |

## Najważniejsze poprawki i blokery

### P0 — główna funkcja produktu

Brakuje agentów, które jadą, otrzymują dyrektywy i omijają obserwowany przedmiot. NavMesh i `NavMeshObstacle.carving = true` są fundamentem. Bez wykonania misji nie ma kompletnego demonstratora OmniTwin.

Dowody: [skrypty klienta](../unity/client/Runtime), [budowa sceny](../unity/OmniTwin.Unity/Assets/_OmniTwin/Editor/TacticalSceneBuilder.cs), [kontrakty celów](../src/OmniTwin.Contracts/AgentTargetDirective.cs). Dalej trzeba połączyć symulowane roboty z walidowanymi celami i zatrzymaniem przy utracie wiarygodnego źródła. Symulowane roboty są elementem produktu; nie należy udawać ich testowymi przeszkodami detekcji SAM.

### P0 — sprzęt, klucz i identyfikator modelu

W sprawdzanym środowisku procesu oraz ustawieniach użytkownika oba klucze były nieustawione. Sprawdzono obecność bez wypisywania wartości. Nie potwierdzono dostępu do modeli i dokładności fizycznego stołu. Użytkownik tworzy klucz i wpisuje go lokalnie; [dokładne kroki](api-keys.md).

Specyfikacja wymaga `meta-models/muse-spark-open-1026`. Oficjalne przykłady [Meta](https://dev.meta.ai/docs/muse-spark-open/deploy) i [Baseten](https://www.baseten.co/resources/changelog/muse-spark-open-and-nemotron-35-super-vl-available-on-baseten/) podają `meta-models/muse-spark-open`. Dostępność wariantu z datą trzeba potwierdzić uwierzytelnionym żądaniem. Projekt zachowuje wymagany ID i nie podmienia go automatycznie.

### P1 — niespójne budżety wieku i czasu API

[SamClientOptions](../src/OmniTwin.Perception.Runtime/Vision/Sam31Client.cs) dopuszcza domyślnie 20 s na próbę i 60 s na operację. [SegmentationWorker](../src/OmniTwin.Perception.Runtime/Vision/SegmentationWorker.cs) akceptuje wiek do 5 s, a [BackendOptions](../src/OmniTwin.Backend/BackendOptions.cs) ustawia retencję 3000 ms. Odpowiedź w wieku np. 4 s może przejść segmentację, a zostać odrzucona przez tracker. Dłuższe wywołanie może zużyć budżet API i nie dostarczyć aktualnej geometrii.

Do poprawy: spójny budżet świeżości całej ścieżki, wcześniejsze anulowanie nieprzydatnych operacji i test opóźnienia 3–5 s oraz ponowień. Bezwarunkowe przedłużanie aktualności starych przeszkód nie rozwiązuje problemu.

### P1 — brak lokalnego trackera obrazu

[FrameChangeTrigger](../src/OmniTwin.Perception/Vision/FrameChangeTrigger.cs) wybiera klatki do SAM, a [ObstacleTracker](../src/OmniTwin.Perception/Tracking/ObstacleTracker.cs) kojarzy obserwacje. Jego dokumentacja i kod zachowują ostatnie zaobserwowane pozycje. Brakuje lokalnego pomiaru ruchu z obrazu pomiędzy odpowiedziami API.

Do wdrożenia: tracker obrazu, ocena jakości, korekcja przez SAM, jawne pochodzenie i wygasanie obserwacji. Zmierzyć FPS i CPU; pierwotne „zerowe opóźnienie” nie jest poprawnym kryterium.

### P1 — zbyt ogólna diagnostyka klucza

`SamApiException` i CLI `segment` zachowują kod HTTP. [SegmentationWorker](../src/OmniTwin.Perception.Runtime/Vision/SegmentationWorker.cs) przekazuje dalej tylko `Category`; 401 i 403 mogą wyglądać tak samo jako `http_error`. HUD pokazuje ogólny stan strumienia.

Do poprawy: bezpieczne kategorie uwierzytelnienia, uprawnień, limitów i timeoutów aż do statusu/HUD, bez tokena i surowej odpowiedzi dostawcy. Na teraz osobne `segment` z instrukcji kluczy ułatwia diagnozę.

### P1 — uruchomienie i dowód wydania

Systemowy SDK 10.0.401 jest starszy od wymaganego 10.0.403. Testy używały osobnego, zweryfikowanego SDK. [Instrukcja](api-keys.md) podaje instalację i istniejącą lokalną ścieżkę. GitHub Verify dla `4130323` zakończył się przed testami z powodu ograniczenia płatności/spending limit konta: [przebieg](https://github.com/Mr444-dev/OmniTwin/actions/runs/41128170265). Wyniki kompilacji i testów są lokalne.

## Co już poprawiono względem szkicu

- .NET 8 zastąpiono .NET 10 LTS, zachowując .NET Standard 2.1 dla Unity i przypięte zależności.
- Homografia odrzuca błędne punkty/projekcje zamiast zwracać wiarygodnie wyglądające `(0, 0)`; sprawdza geometrię, skończoność i obszar.
- DTO przejmują własność kolekcji; strumień ma sesje/kalibrację, kolejność i świeżość.
- Kamera/API są poza głównym wątkiem Unity; kolejki są ograniczone i stare wyniki odrzucane.
- SAM używa udokumentowanego protokołu i oficjalnych próbek kodeka.
- Utworzono rzeczywistą scenę i HUD, naprawiono układ UI Toolkit/font, sprawdzono JSON w runtime Unity i dodano ochronę lokalnych zmian przy imporcie DLL/skryptów.
- Dodano lokalny kalibrator i eksport profilu; własne zdjęcia/profile są ignorowane w `local/`.
- Skorygowano założenia: AST nie jest sandboxem, carving nie gwarantuje reakcji w następnej klatce, ukryty tok rozumowania nie jest wymaganym logiem. Brain pozostaje do wykonania.

## Kolejność dalszej pracy i kryteria odbioru

| Etap | Praca | Dowód zakończenia |
| --- | --- | --- |
| 1. Prawdziwa percepcja | Klucz, własny profil, `segment`, poprawa budżetów i diagnostyki | Aktualna obserwacja, zmierzone punkty i pozycja prawdziwego przedmiotu w Unity |
| 2. System lokalny | Tracker obrazu i wykonanie celów robotów, NavMesh, zatrzymanie | Zmiana geometrii podczas oczekiwania na SAM; robot reaguje także na awarię źródła |
| 3. Misje i fallback | Trzy polityki, parametry poleceń, ground/aerial, telemetria | Testy matematyki/limitów i rzeczywiście wykonywana misja w scenie |
| 4. Brain | Potwierdzenie ID Baseten, klient, walidacja planu, trzy próby, Roslyn, izolacja OS | Komenda tworzy zweryfikowaną politykę; błąd/timeout/złe dyrektywy prowadzą do sprawdzonego fallbacku |
| 5. Interakcja | Tekst, nagranie/STT, PIP, telemetria i logi | Misja zadana głosem i tekstem; awaria mikrofonu nie blokuje obsługi |
| 6. Rozszerzenia Meta | Semantyka nowych obiektów, Muse Image/scenerie | Prawdziwe wywołania z udokumentowanym formatem, limitami i obsługą awarii |
| 7. Wydanie | Standalone, profilowanie, długi test, CI, instrukcja i nagranie | Powtarzalny pełny scenariusz na docelowym sprzęcie, zmierzone FPS/opóźnienia i jawne ograniczenia |

Nowe projekty i pliki dodajemy z działającą implementacją. Nie tworzymy pustych adapterów i przycisków udających integrację.

## Najbliższe działania użytkownika i projektu

**Użytkownik:** konto/dostęp do modeli, utworzenie i lokalne wpisanie klucza, fizyczne ustawienie kamery, wskazanie zmierzonych narożników i pierwsze prawdziwe żądanie. Rozliczenia GitHub wymagają działania właściciela konta.

**Implementacja:** pozostałe moduły, poprawki, testy, diagnostyka, dokumentacja i synchronizacja prywatnego repozytorium. Pisanie/testy pozostałego kodu mogą trwać bez znajomości wartości klucza.

[Wyniki i granice weryfikacji](verification.md). Dotychczasowe dowody nie obejmują benchmarku 60 FPS ani kompletnego pokazu misji roju.
