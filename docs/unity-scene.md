# Uruchomienie gotowej sceny OmniTwin

Projekt jest już utworzony, zaimportowany i podłączony: **`unity/OmniTwin.Unity`**, edytor **6000.6.3f1**, URP **17.6.0**. Otwórz scenę **`Assets/_OmniTwin/Scenes/TacticalDesk.unity`**. Nie musisz ponownie uruchamiać importera ani ręcznie podpinać komponentów.

Scena zawiera podłogę 20 × 12,5 m, siatkę X/Z, zapisany NavMesh, 3 roboty naziemne i 2 drony, ścieżki LineRenderer, kamerę, URP, odbiornik świata, lokalny klient telemetrii/sterowania oraz HUD UI Toolkit. Wymiary podłogi muszą odpowiadać kalibracji. Roboty są rzeczywistymi obiektami symulacji, a nie danymi udającymi pomiar kamery.

1. Z głównego folderu repozytorium uruchom backend:

   ```powershell
   dotnet run --project src/OmniTwin.Backend -c Release -- --OmniTwin:EnablePerception=false
   ```

2. W Unity naciśnij **Play**. Przycisk **RECONNECT STREAM** ponownie otwiera połączenie z `ws://127.0.0.1:5108/ws/twin`. Przy braku obserwacji pola pokazują `--`, a HUD informuje o braku aktualnych danych. Po wyczerpaniu ograniczonych prób/limitu ciszy wyświetla błąd połączenia; nie jest to potwierdzenie pustego stołu.
3. Kamerę obracasz przeciąganiem prawym przyciskiem myszy. Kółko zmienia odległość; **Home** przywraca widok początkowy.
4. Aby otrzymać prawdziwe przeszkody, skonfiguruj `META_API_KEY`, kamerę i cztery zmierzone punkty kalibracji zgodnie z [instrukcją backendu](backend.md). Zatrzymaj wcześniej uruchomiony backend i uruchom go z percepcją włączoną. Klucz zostaje w środowisku procesu backendu.

HUD udostępnia Patrol/Cordon/Grid/Hold, tekst misji i **SYNTHESIZE POLICY (API)**. Cztery pierwsze przyciski wybierają kod wbudowany; synteza wymaga BASETEN_API_KEY tylko w backendzie. Hold anuluje także generowanie. Stan źródła, pięć pomiarów agentów, model/fallback i diagnostyka Roslyn są widoczne w HUD. Bez świeżej percepcji lub kontroli wszystkie jednostki zatrzymują się. [Konfiguracja obu kluczy](api-keys.md).

Żądania do Meta/Baseten na koncie użytkownika nadal wymagają odbioru. Scena nie otrzymuje zastępczych detekcji. Kontrolowany test nawigacyjny uruchamiał prawdziwe kontrolery wokół jawnie oznaczonej tymczasowej przeszkody, poza strumieniem SAM. [Dowody i ograniczenia](verification.md).

Sprawdzone w Editorze: kompilacja, rosnący licznik klatek w Play mode, odczyt kontraktów/JSON na Mono, odrzucenie błędnego komunikatu, kliknięcie przycisku reconnect z przejściem do `Connected`, brak aktualności przy wyłączonej percepcji, reakcja kamery na kółko oraz zapisany NavMesh. Nie wykonano jeszcze pomiaru FPS, testu IL2CPP, builda standalone ani testu kamery/API.

Scenę utworzono przez Editor API z zachowaniem sceny szablonu. Narzędzie `OmniTwin → Create Tactical Desk Scene` służy do utworzenia nowej sceny, kiedy nie istnieje; istniejącego pliku nie nadpisuje. Źródła HUD, kamera i narzędzie autora są w `Assets/_OmniTwin`; bazowe źródła adaptera transportu są w `unity/client` i są importowane przez istniejący skrypt. Wszystkie assety należy zachowywać wraz z `.meta`.
