# Klucze API i pierwsze uruchomienie

Obecna aplikacja korzysta z **META_API_KEY dla SAM 3.1**. Adapter Baseten, głos i pozostałe modele nie są jeszcze zaimplementowane. Ustawienie `BASETEN_API_KEY` nie uruchomi planowania ani robotów. Aktualna lista funkcji jest w [audycie projektu](project-audit.md).

Klucz wpisujesz lokalnie w terminalu. Nie przesyłaj jego wartości do czatu, Unity, kalibratora ani GitHuba. Nasz backend czyta zmienne środowiskowe; plik `.env.example` jest wzorem i nie jest automatycznie ładowany. Prywatne repozytorium również nie jest miejscem na sekrety.

## 1. Skąd wziąć klucz Meta

1. Otwórz [Meta Model API](https://dev.meta.ai/), zaloguj się na swoje konto i wybierz właściwy projekt/zespół.
2. Przejdź do **API keys**, wybierz **Create API key**, nadaj nazwę np. `OmniTwin-local` i skopiuj klucz.
3. Sprawdź na swoim koncie dostęp do `sam-3.1`, limit i możliwość wykonywania żądań. Samo utworzenie klucza nie potwierdza dostępu do każdego modelu.

Kroki tworzenia klucza i uwierzytelnienie Bearer opisuje [oficjalna instrukcja Meta](https://dev.meta.ai/docs/authentication). Oficjalne przykłady używają nazwy zmiennej `MODEL_API_KEY`; **OmniTwin oczekuje META_API_KEY**. To nazwa zmiennej w naszym programie, a nie inny rodzaj klucza. SAM używa tego samego uwierzytelnienia przez [Responses API](https://dev.meta.ai/docs/sam/overview).

Nie tworzę konta ani klucza za Ciebie. Wklejenie go w maskowanym polu niżej wystarcza do konfiguracji programu.

## 2. Ustaw klucz w terminalu backendu

Otwórz PowerShell w katalogu `OmniTwin`, zawierającym `OmniTwin.sln`:

```powershell
.\tools\SetOmniTwinApiKey.ps1 -Provider Meta
```

Po komunikacie `Paste META_API_KEY` wklej klucz i naciśnij Enter. Wpis jest maskowany. Wartość nie trafia do argumentów polecenia, pliku ani komunikatu skryptu. Jest dostępna jako zwykła zmienna środowiskowa procesu, z której backend musi odczytać token.

Sprawdź wyłącznie obecność klucza:

```powershell
.\tools\SetOmniTwinApiKey.ps1 -Provider Meta -Action Status
```

`IsSet = True` oznacza konfigurację tego terminala. Nie jest to test uprawnień ani odpowiedzi API. Uruchom backend **w tym samym terminalu**. Już działający backend oraz inne okna PowerShell nie otrzymają nowej wartości. Po zamknięciu terminala ustaw ją ponownie; skrypt nie zmienia ustawień użytkownika ani komputera.

Nie wpisuj literalnego klucza jako `$env:META_API_KEY = '...'`: treść takiego polecenia może zostać w historii. Jeśli organizacyjna polityka blokuje uruchamianie `.ps1`, nie trzeba zmieniać ustawień bezpieczeństwa; poniższe pojedyncze polecenie wykonuje maskowany odczyt w bieżącym terminalu:

```powershell
$env:META_API_KEY = [Net.NetworkCredential]::new('', (Read-Host 'META_API_KEY' -AsSecureString)).Password
```

## 3. SDK i lokalna kalibracja

Projekt wymaga SDK 10.0.403 lub nowszej stabilnej poprawki z pasma 10.0.4xx zgodnie z `global.json`. W czasie audytu systemowy SDK tej maszyny wynosił 10.0.401. Możesz zainstalować [wymagany SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) albo w tej konkretnej sesji skorzystać z już zweryfikowanego SDK w katalogu roboczym:

```powershell
# Od katalogu repozytorium. Ta ścieżka dotyczy obecnego stanowiska.
$env:DOTNET_ROOT = (Resolve-Path ..\..\work\dotnet-10.0.403).Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
```

Wynik powinien wskazywać 10.0.403. Ten katalog roboczy nie jest częścią repozytorium i nie zastępuje instalacji SDK na innym komputerze.

Najpierw, bez klucza i wywołań API, wykonaj [kalibrację](calibration.md). Potrzebujesz własnego `local/calibration.json` i niezmiennej pozycji kamery. Nie uruchamiaj percepcji na przykładowym profilu jako pomiarze swojego stołu.

## 4. Najpierw sprawdź jedno prawdziwe żądanie

Po zapisaniu `local/table.jpg`, ustawieniu klucza i wybraniu SDK:

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- segment local/table.jpg cup
```

To wysyła zdjęcie do Meta i może zużywać kredyty. Zatwierdzony wynik zawiera maski/boxy albo poprawny pusty wynik, jeśli model nie wykryje kubka. Ten krok oddziela problem klucza/modelu od ustawień kamery i Unity.

Klient wykonuje ograniczone ponowienia błędów przejściowych. Nie ponawia błędów uwierzytelnienia. W razie błędu CLI wypisuje kategorię, liczbę prób i kod HTTP, bez wartości klucza i bez treści odpowiedzi dostawcy. Ponowione żądania mogą ponownie naliczać koszt.

| Wynik | Co sprawdzić |
| --- | --- |
| Brak `META_API_KEY` | Ustaw klucz w tym samym terminalu, z którego startuje program |
| HTTP 401 | Poprawność, ważność i projekt klucza |
| HTTP 403 | Uprawnienia konta/projektu do modelu |
| HTTP 429 | Aktualne limity, kredyty i wykorzystanie konta |
| `invalid_response` | Zgodność odpowiedzi dostawcy z parserem; nie pomijaj walidacji |
| `attempt_timeout` / `total_timeout` | Sieć, opóźnienie dostawcy i budżet operacji |

Kod HTTP pomaga w diagnozie, ale sam nie dowodzi konkretnej przyczyny po stronie konta.

## 5. Uruchom percepcję i Unity

Zatrzymaj backend używany do kalibracji. W terminalu z ustawionym kluczem:

```powershell
$env:OmniTwin__CalibrationPath = (Resolve-Path .\local\calibration.json).Path
$env:OmniTwin__CameraDevice = '0'
$env:OmniTwin__Concept = 'cup'
dotnet run --project src/OmniTwin.Backend -c Release -- --OmniTwin:EnablePerception=true
```

CLI `true` jawnie włącza kamerę i żądania SAM. W innej karcie przeglądarki otwórz [status backendu](http://127.0.0.1:5108/api/status). `disabled` oznacza wyłączoną percepcję, `starting` jej start, `running` pracę źródła, `degraded` błąd operacji/odrzucone detekcje, a `faulted` zakończenie źródła z błędem. Sprawdzaj również `isObservationCurrent`; sam status połączenia nie potwierdza aktualności geometrii.

Otwórz `unity/OmniTwin.Unity` w Unity **6000.6.3f1**, następnie scenę `Assets/_OmniTwin/Scenes/TacticalDesk.unity`, Play i przycisk ponownego połączenia w HUD. [Instrukcja sceny](unity-scene.md). Na obecnym etapie zobaczysz obserwowane przeszkody i stan strumienia. Sterowanie robotami i komendy głosowe wymagają dalszej implementacji.

Po pracy zatrzymaj backend, a następnie usuń klucz z terminala:

```powershell
.\tools\SetOmniTwinApiKey.ps1 -Provider Meta -Action Clear
```

Usunięcie zmiennej nie odbiera klucza już uruchomionemu procesowi. Unieważnienie klucza na koncie Meta jest osobną operacją wykonywaną w panelu dostawcy.

## Baseten — konfiguracja przyszłego modułu

Baseten opisuje tworzenie klucza w [Settings → API keys](https://docs.baseten.co/organization/api-keys). Klucz tego dostawcy będzie należał do `BASETEN_API_KEY`. Skrypt obsługuje `-Provider Baseten`, ale obecny backend nie odczytuje tej zmiennej i nie wysyła żądań planowania.

Specyfikacja wymaga dokładnie `meta-models/muse-spark-open-1026`. [Publiczna instrukcja Meta dla Baseten](https://dev.meta.ai/docs/muse-spark-open/deploy) dokumentuje `meta-models/muse-spark-open`. Nie potwierdziliśmy uwierzytelnionym żądaniem, że wariant z `-1026` jest dostępny dla Twojego konta. Trzeba to zweryfikować przed uruchomieniem Brain; projekt zachowuje wymagany identyfikator i nie podmienia go automatycznie.
