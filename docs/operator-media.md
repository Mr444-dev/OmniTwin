# Komendy głosowe i podgląd kamery

Etap 5 dodaje rzeczywisty adapter **Muse Voice Transcribe** oraz podgląd istniejącej kamery w HUD. Głos tworzy edytowalną treść misji. Sam wynik rozpoznawania nie wysyła rozkazów do agentów; użyj istniejącego przycisku **SYNTHESIZE POLICY (API)** po sprawdzeniu tekstu. Polityki i EmergencyHold zachowują własne warunki świeżości.

## Uruchomienie

1. W terminalu backendu ustaw `META_API_KEY` przez `./tools/SetOmniTwinApiKey.ps1 -Provider Meta`. Ten sam klucz obsługuje SAM i Muse Voice, ale konto musi mieć dostęp do obu modeli. [Instrukcja kluczy](api-keys.md).
2. Otwórz scenę `Assets/_OmniTwin/Scenes/TacticalDesk.unity` w Unity **6000.6.3f1** i uruchom backend/Play według [instrukcji sceny](unity-scene.md).
3. W panelu roju wybierz **RECORD VOICE (META API)**. Kolejne kliknięcie kończy nagrywanie i wysyła je do transkrypcji. Alternatywnie przytrzymaj **Spację**, gdy żaden element UI nie ma fokusu. Zwolnienie kończy nagranie. Spacja podczas edycji tekstu działa normalnie.
4. Nagranie trwa od 0,2 do maksymalnie 5 sekund. Automatycznie zatrzymuje się na limicie; utrata fokusu/wyłączenie komponentu anuluje nagrywanie. Używany jest pierwszy dostępny mikrofon zgłoszony przez Unity. Brak urządzenia lub błąd odczytu pozostawia pole tekstowe i przyciski misji dostępne.
5. Rozpoznany tekst pojawia się w polu instrukcji, jeżeli nie zmieniłeś go podczas nagrywania/transkrypcji. Ręczne zmiany mają pierwszeństwo. **USE TRANSCRIPT** pozwala jawnie przenieść wynik do pola. Pusty wynik oznacza brak rozpoznanej mowy.
6. **CANCEL VOICE** zatrzymuje lokalne nagrywanie/oczekiwanie. Transkrypcja przyjęta już przez backend może zakończyć się na serwerze. Nie wykonuje to misji i nie omija Hold.

Nagrania nie są zapisywane na dysku ani w repozytorium. Dane istnieją w ograniczonych buforach pamięci; bufory robocze są czyszczone po użyciu. Jawne wysłanie nagrania do Meta może zużywać kredyty. Nie wpisuj klucza do Unity.

## Format i odporność głosu

Unity pozyskuje PCM przez swoje API na głównym wątku. Kodowanie i resampling działają w tle. `PcmWaveCodec` przyjmuje skończone próbki mono/stereo 8–96 kHz, miesza kanały i używa filtru windowed-sinc przy zmianie częstotliwości. Wyjściem jest kanoniczny **RIFF/WAVE PCM16 mono 16 kHz**, maksymalnie 160044 bajty. Serwer odrzuca inne nagłówki, długości, niepełne pliki i przekroczony czas nagrania przed wywołaniem dostawcy. Zachowanie natywnego startu mikrofonu wymaga pomiaru na sprzęcie użytkownika.

`POST /api/voice/transcribe` wymaga `audio/wav`, `X-OmniTwin-Media: 1` i unikalnego GUID `X-OmniTwin-Voice-Request`. Obowiązują loopback/same-origin, dwusekundowy limit uploadu i limit rozmiaru. Jedna transkrypcja działa naraz. Identyczne ponowienia GUID + SHA-256 dołączają do tego samego wyniku; inna zawartość pod tym ID daje 409. Cache przechowuje do 16 wyników przez maksymalnie pięć minut, bez audio. Nowe nagranie podczas zajętości daje 429. Restart/wyparcie cache nie zapewnia deduplikacji.

Adapter wysyła `multipart/form-data` do `https://api.meta.ai/v1/asr/transcribe`: `request` z modelem `muse-voice-transcribe-1.0`, `audioEncoding=WAV`, `mode=PUSH_TO_TALK`, biasem Polish/English, oraz część `audio`. Używa Bearer i JSON. Odpowiedź musi zawierać dokładnie `sessionId`, `transcript`, `audioDurationMs`, `turns`; identyfikator musi się zgadzać, czas mieścić w tolerancji 100 ms, a `turns` być puste dla tego trybu. Sesja dostawcy jest korelacją, nie gwarancją idempotencji. [Oficjalny endpoint](https://dev.meta.ai/docs/api-reference/voice/transcribe), [schemat](https://dev.meta.ai/docs/api-reference/voice/schemas), [tryby](https://dev.meta.ai/docs/speech-to-text).

Trzy próby przejściowych błędów mieszczą się w 30-sekundowym budżecie; pojedyncza próba ma 12 sekund, a odpowiedź limit 32 KiB. Ponowienia używają exponential backoff i `Retry-After`; 401/403 nie są ponawiane. Utracona odpowiedź/timeout może oznaczać ponowny koszt u dostawcy. Do HUD trafiają wyłącznie bezpieczne kategorie, np. `auth_missing_or_invalid_401`, `permission_denied_403`, `rate_limited_429`, `upstream_timeout`, `network_error`. Surowe odpowiedzi, audio, tokeny i transkrypcje nie są logowane przez adapter.

## Kamera PIP

**SHOW CAMERA PREVIEW** pobiera podgląd; **HIDE CAMERA PREVIEW** go usuwa. Nie otwiera drugiej kamery. Backend musi mieć włączoną rzeczywistą percepcję, klucz SAM i własną kalibrację. Przy źródle wyłączonym HUD pokazuje niedostępność, bez obrazu zastępczego. W panelach można przewijać zawartość.

`CameraPreviewStore` wykorzystuje klatki istniejącego `CameraPerceptionSource`, koduje najwyżej 5 podglądów/s i zachowuje tylko najnowszy. Obraz ma najdłuższy bok do 320 px i maksymalnie 128 KiB. Błąd opcjonalnego kodera podglądu nie zatrzymuje nawigacji. `GET /api/camera/preview` jest poprzednim endpointem JPEG; HUD używa obecnie `GET /api/camera/annotated`, który zwraca JPEG i przezroczysty PNG masek w jednym zamkniętym JSON-ie. Para zawiera wspólną sesję, kalibrację, numer klatki, czas i wymiary.

Klient sprawdza świeżość, rozmiar, bezpieczny kontener JPEG/PNG, wymiary i pochodzenie masek, zanim Unity dekoduje teksturę. Klatka znika po 750 ms bez świeżego pomiaru. HTTP działa w tle; aktualizacja tekstur jest na wątku Unity. Warstwa masek jest osobno przełączana. Ciągły obrys to maska z tej samej klatki SAM; przerywany to maska przesunięta pomiarem lokalnego ruchu, a etykieta pokazuje wiek SAM. Kolor wyróżnia strefę oznaczoną jako hazard. Nie nakładamy starszej maski jako świeżej.

LiDAR skanuje w `FixedUpdate` 48 wiązkami 20 razy/s, niezależnie od przełącznika wizualizacji. HUD może pokazać wiązki i punkty trafień, a telemetria podaje minimum i wiek próbki. To sensor colliderów w scenie Unity, a nie odczyt z urządzenia LiDAR. Ręczne tagi semantyczne dotyczą symulacji. Opis i zabezpieczenia generowania tekstur znajdują się w [systemach wizualnych](visual-systems.md).

## Weryfikacja i ograniczenia

Testy obejmują oba targety bibliotek, format/długość PCM, resampling, PIP i pochodzenie maski, lokalny protokół Meta, semantyczne potwierdzenie/override, CRC/limity PNG/JPEG, polskie znaki, ścisły JSON i bezpieczne błędy API. Lokalne serwery Kestrel sprawdzają całą ścieżkę klient → backend → kontrolowany protokół dostawcy, deduplikację, konflikty ID, limity, origin i expiry. Dane audio/obrazu testów są syntetyczne i nie trafiają do strumienia nawigacji. [Wyniki oraz pozostały odbiór](verification.md).

To nie jest dowód nagrania fizycznego mikrofonu ani dostępu do live Meta API. Takie próby, dokładność polskiej mowy, rzeczywisty obraz stołu i opóźnienia wymagają urządzeń oraz klucza użytkownika. [Aktualne wyniki](verification.md), [raport odbioru](project-audit.md).
