# Stan odbioru OmniTwin — 31 października 2026

Wdrożono percepcję P1, rzeczywisty rój, Brain z izolowanym wykonawcą oraz kolejny etap: komendy głosowe i podgląd kamery w HUD. **Nie oznacza to ukończenia całej pierwotnej specyfikacji produktu.** Dowody poniżej oddzielają sprawdzony kod od odbioru na fizycznym stole i płatnych usługach.

| Etap / wcześniejszy bloker | Wdrożenie | Dowód i pozostały odbiór |
| --- | --- | --- |
| 1 / P1 budżety | SAM 6 s/próbę, 12 s/operację i pozostały użyteczny wiek klatki 12 s; retencja 20 s | Test 5-sekundowego opóźnienia, anulowania i granic wieku; pozostaje pomiar live API |
| 1 / P1 diagnostyka | Jawne 401/403/429, timeout i network error przez worker, status i sterowanie do HUD | Testy adaptera i rzeczywistego klienta Kestrel, bez tokenów i surowych ciał błędów |
| 1 / P1 Hybrid Vision | Sparse optical flow OpenCV w ROI, kontrola przód/tył i jakości, X/Z, korekcja opóźnionego SAM | Natywne testy translacji, utraty tekstury i korekcji; ObservationSource oraz osobny wiek geometrii; protokół v2 |
| 1 / świeżość | Lokalne pomiary nie odnawiają wieku SAM; lokalna geometria i heartbeat kamery wygasają po 1 s | Retencja 20 s nie ukrywa utraty kamery; błąd źródła zatrzymuje ruch |
| 2–3 / P0 polityki | Patrol obwodu, kwarantanna największego zagrożenia/obiektu, skanowanie siatki, EmergencyHold | Determinizm, agent IDs, obszar, przeszkody i brak bezpiecznej drogi testowane na obu targetach |
| 2–3 / P0 rój | 3 NavMesh AGV i 2 drony, ścieżki, carving, guard fizyki i pomiary telemetrii symulacji | Ruch pięciu jednostek w 1307 klatkach i ścieżki każdej; backend odebrał ich telemetrię. Jawna testowa przeszkoda nawigacyjna nie udawała SAM |
| 2–3 / sterowanie | Asynchroniczne telemetry/mission/status, tekst i przyciski, korelacja sesji/kalibracji | Testy Kestrel/klienta i idempotencji; wyłączona percepcja oznacza 5 zatrzymanych jednostek |
| 4 / P0 Baseten | Rzeczywisty klient z BASETEN_API_KEY; datowany ID i alias wyłącznie po 404 | Lokalne HTTP potwierdza fallback i brak zamiany przy 401/403/429; pozostaje żądanie na koncie użytkownika |
| 4 / P0 Roslyn | SemanticModel, jawna lista symboli, limity źródła, 0 błędów/ostrzeżeń, maks. 3 kandydatów | Testy poprawnego kodu, pełnych nazw, reflection/dynamic/I/O oraz powrotu diagnostyki do naprawy |
| 4 / P0 izolacja | AppContainer bez capabilities, Job Object, brak kluczy, ograniczone IPC, 100 ms compute, limity pamięci/CPU/procesów/lifetime | Prawdziwe testy OS: token, odmowa pliku, blokada loopback, pętla, presja pamięci, wyjątek konstruktora i NaN/Infinity |
| 4 / fallback | Nieudana synteza/wykonanie → wybrana polityka wbudowana; utrata percepcji → Hold | Test koordynatora z rzeczywistym izolowanym procesem i timeoutem |
| 5 / głos | Jawne nagrywanie do 5 s, PCM16/16 kHz, rzeczywisty adapter Muse Voice, retry/deduplikacja i edytowalny tekst | Testy kodeka i całej ścieżki HTTP; Spacja w polu instrukcji nie włącza mikrofonu. Fizyczny mikrofon i live API wymagają odbioru |
| 5 / PIP | Podgląd z istniejącego źródła OpenCV, do 320 px/5 Hz, wygasanie 750 ms, bez dodatkowej kamery | Natywne JPEG i testy świeżości; HUD prawidłowo pokazuje brak obrazu przy wyłączonej percepcji. Rzeczywisty obraz stołu wymaga odbioru |
| Weryfikacja | 411 testów .NET w pięciu zestawach; Unity Mono/Play/NavMesh/Hold i HUD mediów | Zestaw lokalny przechodzi; [szczegóły](verification.md). Hostowane CI podlega ograniczeniom konta |

## Co nadal wymaga pracy

- **Odbiór sprzętowy P0:** klucze i dostęp do modeli, własna kalibracja, prawdziwe SAM i Baseten oraz przejazd wokół przedmiotu obserwowanego kamerą. [Instrukcja](api-keys.md).
- **Odbiór głosu i PIP:** sprawdzenie uprawnień/urządzenia mikrofonu, polskiej transkrypcji na koncie Meta i podglądu fizycznego stołu. Kod i ścieżka lokalna są wdrożone; [obsługa operatora](operator-media.md). Nakładka masek SAM zsynchronizowana z obrazem nie jest jeszcze wdrożona.
- **Semantyka i scenerie:** klasyfikacja multimodalna, pamięć nowych obiektów i Muse Image/scenerie pozostają do wdrożenia. Hazard jest oznaczeniem operatora.
- **LiDAR:** agent ma lokalną kontrolę kolizji; pełny sensor i wizualizacja LiDAR pozostają do wdrożenia.
- **Wydanie:** standalone/IL2CPP, długi test, pomiar FPS/opóźnień, profilowanie i nagranie scenariusza. 60 FPS to cel, nie wynik benchmarku.
- **GitHub Actions:** [przebieg etapu 5](https://github.com/Mr444-dev/OmniTwin/actions/runs/41205766849) ponownie zablokowały płatności/limit konta przed uruchomieniem zadania. Lokalnie przechodzi 411 testów; hostowane testy nie wystartowały. Rozliczenia wymagają działania właściciela konta.

## Uruchomienie i granice

Otwórz istniejącą scenę Unity **6000.6.3f1 / URP**, ustaw oba klucze w terminalu backendu i własny profil stołu według [instrukcji](api-keys.md). W Play dostępne są Patrol/Cordon/Grid/Hold, nagrywanie, przełącznik podglądu oraz jawny przycisk syntezy API. Sprawdź treść transkrypcji przed syntezą; głos sam nie wydaje rozkazu. Bez wiarygodnej percepcji jednostki pozostają w Hold. Klucze nie trafiają do Unity.

[Brain i izolacja](brain.md), [tracking](tracking.md), [protokół backendu](backend.md), [weryfikacja](verification.md). Nie podajemy procentu ukończenia: cały pierwotny zakres jest większy niż te trzy moduły.
