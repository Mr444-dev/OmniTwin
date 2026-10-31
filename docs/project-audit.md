# Stan odbioru OmniTwin — 31 października 2026

Wdrożono percepcję P1, rzeczywisty rój, Brain z izolowanym wykonawcą, komendy głosowe, PIP, wizualne systemy oraz etap 7 z jawnym replayem offline i preflightem. **Nie oznacza to ukończenia całej pierwotnej specyfikacji produktu.** Dowody poniżej oddzielają sprawdzony kod od odbioru na fizycznym stole i płatnych usługach.

| Etap / wcześniejszy bloker | Wdrożenie | Dowód i pozostały odbiór |
| --- | --- | --- |
| 1 / P1 budżety | SAM 6 s/próbę, 12 s/operację i pozostały użyteczny wiek klatki 12 s; retencja 20 s | Test 5-sekundowego opóźnienia, anulowania i granic wieku; pozostaje pomiar live API |
| 1 / P1 diagnostyka | Jawne 401/403/429, timeout i network error przez worker, status i sterowanie do HUD | Testy adaptera i rzeczywistego klienta Kestrel, bez tokenów i surowych ciał błędów |
| 1 / P1 Hybrid Vision | Sparse optical flow OpenCV w ROI, kontrola przód/tył i jakości, X/Z, korekcja opóźnionego SAM | Natywne testy translacji, utraty tekstury i korekcji; ObservationSource oraz osobny wiek geometrii; protokół v3 z jawnym sourceMode |
| 1 / świeżość | Lokalne pomiary nie odnawiają wieku SAM; lokalna geometria i heartbeat kamery wygasają po 1 s | Retencja 20 s nie ukrywa utraty kamery; błąd źródła zatrzymuje ruch |
| 2–3 / P0 polityki | Patrol obwodu, kwarantanna największego zagrożenia/obiektu, skanowanie siatki, EmergencyHold | Determinizm, agent IDs, obszar, przeszkody i brak bezpiecznej drogi testowane na obu targetach |
| 2–3 / P0 rój | 3 NavMesh AGV i 2 drony, ścieżki, carving, guard fizyki i pomiary telemetrii symulacji | Ruch pięciu jednostek w 1307 klatkach i ścieżki każdej; backend odebrał ich telemetrię. Jawna testowa przeszkoda nawigacyjna nie udawała SAM |
| 2–3 / sterowanie | Asynchroniczne telemetry/mission/status, tekst i przyciski, korelacja sesji/kalibracji | Testy Kestrel/klienta i idempotencji; wyłączona percepcja oznacza 5 zatrzymanych jednostek |
| 4 / P0 Baseten | Rzeczywisty klient z BASETEN_API_KEY; datowany ID i alias wyłącznie po 404 | Lokalne HTTP potwierdza fallback i brak zamiany przy 401/403/429; pozostaje żądanie na koncie użytkownika |
| 4 / P0 Roslyn | SemanticModel, jawna lista symboli, limity źródła, 0 błędów/ostrzeżeń, maks. 3 kandydatów | Testy poprawnego kodu, pełnych nazw, reflection/dynamic/I/O oraz powrotu diagnostyki do naprawy |
| 4 / P0 izolacja | AppContainer bez capabilities, Job Object, brak kluczy, ograniczone IPC, 100 ms compute, limity pamięci/CPU/procesów/lifetime | Prawdziwe testy OS: token, odmowa pliku, blokada loopback, pętla, presja pamięci, wyjątek konstruktora i NaN/Infinity |
| 4 / fallback | Nieudana synteza/wykonanie → wybrana polityka wbudowana; utrata percepcji → Hold | Test koordynatora z rzeczywistym izolowanym procesem i timeoutem |
| 5 / głos | Jawne nagrywanie do 5 s, PCM16/16 kHz, rzeczywisty adapter Muse Voice, retry/deduplikacja i edytowalny tekst | Testy kodeka i całej ścieżki HTTP; Spacja w polu instrukcji nie włącza mikrofonu. Fizyczny mikrofon i live API wymagają odbioru |
| 5 / PIP | Podgląd z istniejącego źródła OpenCV, do 320 px/5 Hz, wygasanie 750 ms, bez dodatkowej kamery | Natywne JPEG i testy świeżości; brak źródła jest jawny. PIP nie potwierdza wolnego stołu |
| 6 / maski PIP | Atomowa para obrazu i przezroczystej maski z identyczną klatką, sesją, czasem i rozmiarem | Test integracyjny backend/klient odrzuca złe wymiary i pochodzenie; optical-flow mask jest przerywana i nie odnawia wieku SAM. Unity kompiluje przełącznik nakładki; rzeczywisty kadr wymaga aktywnego backendu/kamery |
| 6 / LiDAR | Każda z 5 jednostek ma 48 wiązek, zasięg 6 m, skan 20 Hz, punkty trafień, widoczność w HUD, minimum i wiek w telemetrii | W Play rzeczywiste skany zwróciły trafienia dla trzech agentów naziemnych (3/6/3) i telemetrię dla wszystkich pięciu; oba drony nie trafiły w przeszkodę w swoim planarnym przekroju. `BlocksTravel` zamyka ruch przy przeterminowanym skanie i sprawdza obrys |
| 6 / semantyka | Po dwóch stabilnych obserwacjach SAM asynchroniczny Muse Spark `muse-spark-1.3` klasyfikuje crop; pamięć ogranicza się do sesji i wspiera nadpisanie operatora | Lokalne Kestrel testy sprawdzają payload obrazu/JSON, próg potwierdzenia, priorytet ręcznego tagu i anulowanie po zmianie sesji; live Meta wymaga klucza |
| 6 / sceneria | Jawny Muse Image `muse-image-1.0`; walidacja rastra, kontrast, bezpieczna podmiana materiału URP i reset | Testy kontrolowanego protokołu i CRC/rozmiaru PNG/JPEG; Unity kompiluje i wiąże controller z powierzchnią. Płatna generacja wymaga klucza operatora |
| 7 / preflight i replay | `live-preflight`; deterministyczne `--synthetic-tabletop-replay`; source mode przesyłany przez v3 i jawny baner w HUD | Lokalny backend odebrał Unity, opublikował 3 syntetyczne tracki i aktualny stan; żaden klucz ani endpoint modelu nie był użyty |
| Weryfikacja | [Raport](verification.md): Release build 0 warning/error, **435/435 testów**, Unity 6000.6.3f1 Play Mode i screenshot z aktualnego Game View | Rzeczywisty stream, 5 agentów, 48-ray LiDAR, 0 błędów/ostrzeżeń w końcowej konsoli; stabilny pomiar w Editorze 125–155 FPS. To krótki pomiar desktopowy, nie benchmark wydania |

## Co nadal wymaga pracy

- **Odbiór sprzętowy P0:** klucze i dostęp do modeli, własna kalibracja, prawdziwe SAM i Baseten oraz przejazd wokół przedmiotu obserwowanego kamerą. Semantyczny hazard jest tagiem symulacji, a nie oceną rzeczywistego niebezpieczeństwa. [Instrukcja](api-keys.md).
- **Odbiór wizualny na żywym źródle:** sprawdzenie zsynchronizowanych masek PIP i wygenerowanej tekstury w Game view z backendem, kluczem Meta i fizyczną kamerą. Weryfikacja kompilacji i Play mode nie jest pomiarem wydajności długotrwałej.
- **Wydanie:** standalone/IL2CPP, długi test, profilowanie GC/opóźnień i nagranie scenariusza. Krótki pomiar Game View w Editorze wyniósł 125–155 FPS; nie zastępuje benchmarku wydania ani testu obciążeniowego.
- **GitHub Actions:** [run #12 dla commitu Stage 6](https://github.com/Mr444-dev/OmniTwin/actions/runs/41242900294) nie uruchomił kroków i nie ma archiwum logów; wcześniejsze adnotacje workflow wskazują limit konta. Lokalnie aktualny zestaw przechodzi 435 testów.

## Uruchomienie i granice

Otwórz istniejącą scenę Unity **6000.6.3f1 / URP**, ustaw wymagane klucze w terminalu backendu i własny profil stołu według [instrukcji](api-keys.md). W Play HUD udostępnia Patrol/Cordon/Grid/Hold, mikrofon, PIP/maski, LiDAR, ręczny tag semantyczny, generator scenerii i jawny przycisk syntezy API. Sprawdź treść transkrypcji przed syntezą; głos sam nie wydaje rozkazu. Bez wiarygodnej percepcji jednostki pozostają w Hold. Klucze nie trafiają do Unity. Szczegóły [systemów wizualnych](visual-systems.md).

[Brain i izolacja](brain.md), [tracking](tracking.md), [protokół backendu](backend.md), [weryfikacja](verification.md). Nie podajemy procentu ukończenia: cały pierwotny zakres jest większy niż te trzy moduły.
