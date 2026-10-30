# Konfiguracja Unity dla OmniTwin

Stan wersji sprawdzony **30 października 2026**. Projekt jest demonstracją 3D na Windows x64: kamera obserwuje stół, osobny backend przelicza geometrię, a Unity docelowo wizualizuje świat i ruch agentów. Utwórz projekt w Hubie według poniższych kroków. Repozytorium zawiera już backend i biblioteki; projekt Unity oraz scena nie zostały jeszcze utworzone ani zweryfikowane w edytorze.

## 1. Edytor i szablon

W Unity Hub zainstaluj **Unity 6.6 — 6000.6.7f1**, stabilne wydanie z 21 października 2026. Wybór realizuje wymaganie aktualnej stabilnej wersji. Gałęzie 6.7 Beta i 7 Alpha są przedpremierowe. [Wydanie Unity](https://unity.com/releases/editor/whats-new/6000.6.7f1), [polityka wsparcia](https://unity.com/releases/unity-6/support).

Na start wystarczy edytor dla Windows i aktywna licencja odpowiednia dla Twojego konta. Moduł **Windows Build Support (IL2CPP)** doinstaluj, jeśli chcesz później budować IL2CPP; ten wariant wymaga również zgodnych narzędzi C++ i Windows SDK. Pierwsza integracja będzie sprawdzana w edytorze i na backendzie Mono.

Wybierz **New project**:

| Pole | Wartość |
| --- | --- |
| Editor version | 6000.6.7f1 |
| Template | **Universal 3D**, pusty projekt URP |
| Project name | **OmniTwin.Unity** |
| Location | Folder `unity` w głównym repozytorium OmniTwin |
| Unity Version Control | Wyłącz przy tworzeniu; używamy istniejącego Git/GitHub |

Szablon Universal 3D ma identyfikator `com.unity.template.urp-blank`. Hub dobiera szablon do wybranego edytora. [Oficjalna instrukcja URP](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/creating-a-new-project-with-urp.html).

W bieżącym środowisku do pola **Location** wklej:

```text
D:\Codex\2026-10-30\omnitwin-architectural-engineering-constitution-strict-mode\outputs\OmniTwin\unity
```

Hub dopisze nazwę projektu. Końcowy katalog powinien być dokładnie:

```text
D:\Codex\2026-10-30\omnitwin-architectural-engineering-constitution-strict-mode\outputs\OmniTwin\unity\OmniTwin.Unity
```

Jeśli pracujesz z klonem repozytorium w innym miejscu, użyj jego folderu `unity`. Repozytorium Git obejmuje backend i Unity wspólnie — nie inicjalizuj drugiego repozytorium wewnątrz `OmniTwin.Unity`.

## 2. Pakiety

Otwórz **Window → Package Management → Package Manager**. W Unity Registry wyszukaj nazwę pakietu albo użyj **Install package by name**. Instaluj wydania stabilne zgodne z wybranym edytorem. Nie edytuj ręcznie manifestu.

| Pakiet | Identyfikator | Wersja sprawdzona | Co zrobić |
| --- | --- | --- | --- |
| Universal RP | `com.unity.render-pipelines.universal` | Wersja dostarczona przez szablon 6000.6 | Pozostaw zgodny zestaw URP, Core RP i Shader Graph |
| AI Navigation | `com.unity.ai.navigation` | **2.0.16** | Dodaj; potrzebny do NavMeshSurface i ruchu po NavMesh |
| Input System | `com.unity.inputsystem` | **1.20.1** | Sprawdź wersję z szablonu; zaktualizuj lub dodaj przez Package Manager |
| Cinemachine | `com.unity.cinemachine` | **3.1.7** | Opcjonalny; przyda się do sterowania kamerą taktyczną |
| UI Toolkit | Wbudowany w edytor | Wersja edytora | Dostępny do planowanego HUD; bez osobnej instalacji |
| Test Framework | `com.unity.test-framework` | Wersja zgodna z edytorem | Zachowaj wersję rozwiązaną przez edytor; potrzebna przy testach klienta |

Numery pakietów sprawdzono w oficjalnym rejestrze Unity. Ostateczna zgodność i rozwiązywanie zależności muszą zostać potwierdzone w Twoim projekcie przez Package Manager. Szczególnie URP nie należy wybierać wyłącznie według tagu `latest` z surowego rejestru: może on wskazywać wersję dla innej generacji edytora. Aktualizacje renderowania robimy zgodnym zestawem obsługiwanym przez Unity 6.6. [Rejestr Navigation](https://packages.unity.com/com.unity.ai.navigation), [Input System](https://packages.unity.com/com.unity.inputsystem), [Cinemachine](https://packages.unity.com/com.unity.cinemachine).

Na obecnym etapie nie są potrzebne pakiety Netcode, DOTS/Entities ani Meta XR SDK. Model SAM działa po stronie backendu przez API; nie wymaga gogli ani SDK XR w Unity. Zakres kolejnych zależności wynika z implementowanej funkcji.

## 3. Ustawienia projektu

W **Edit → Project Settings** sprawdź:

| Miejsce | Ustawienie |
| --- | --- |
| Player → Other Settings → Configuration | API Compatibility Level: **.NET Standard 2.1** |
| Player → Other Settings → Configuration | Scripting Backend dla początkowego builda Windows: **Mono** |
| Player → Other Settings → Configuration | Active Input Handling: **Input System Package (New)**; zaakceptuj restart, jeśli edytor go wymaga |
| Player → Other Settings → Rendering | Color Space: **Linear** |
| Graphics oraz Quality | Zachowaj przypisane assety URP z szablonu; sprawdź aktywny profil jakości |
| Editor | Asset Serialization Mode: **Force Text** |
| Version Control | Mode: **Visible Meta Files** |

W **File → Build Profiles** wybierz **Windows**, architekturę **Intel 64-bit / x86_64**. Nazwy sekcji mogą być grupowane inaczej zależnie od języka interfejsu. Pozostaw domyślny Render Graph z szablonu URP. Zapisz scenę w `Assets/_OmniTwin/Scenes/TacticalDesk.unity`; następnie sprawdź, czy konsola edytora nie zawiera błędów kompilacji, pakietów ani shaderów.

Sam szablon nie zawiera jeszcze komponentów OmniTwin, robotów ani odbiornika WebSocket. Te elementy będą dodane i sprawdzone po utworzeniu projektu. Nie twórz ręcznie pustych skryptów ani zastępczych adapterów.

## 4. Backend i .NET

Zainstaluj **.NET SDK 10.0.403 x64** lub nowszą stabilną poprawkę z pasma 10.0.4xx z [oficjalnej strony .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). SDK zawiera potrzebne runtime'y. Po instalacji otwórz nowy terminal. W głównym folderze repozytorium:

```powershell
dotnet --version
dotnet restore OmniTwin.sln --locked-mode
dotnet build OmniTwin.sln -c Release --no-restore
dotnet test --solution OmniTwin.sln -c Release --no-build --no-restore
dotnet run --project src/OmniTwin.Backend -c Release --no-build -- --OmniTwin:EnablePerception=false
```

Backend nasłuchuje domyślnie na `http://127.0.0.1:5108`. Sprawdź `http://127.0.0.1:5108/health/live` oraz `/api/status`. Przy wyłączonej percepcji readiness i ostatni stan świata zwracają 503; jest to oczekiwany brak danych z kamery. Do prawdziwej percepcji potrzebujesz własnej kalibracji i `META_API_KEY`, zgodnie z [instrukcją backendu](backend.md).

Docelowe połączenie Unity: **`ws://127.0.0.1:5108/ws/twin`**, subprotokół **`omnitwin.v1`**. Jest to własny protokół WebSocket, bez wymogu instalowania SignalR. Obecny backend udostępnia strumień, lecz adapter Unity jest kolejnym modułem. Nie wpisuj kluczy API do Unity, skryptów, scen ani Inspectorów. `BASETEN_API_KEY` będzie potrzebny dopiero przy wdrażaniu modułu Brain.

Backend i narzędzia pracują na .NET 10, a Unity zachowuje własny runtime Mono/IL2CPP i profil .NET Standard 2.1. Do Unity będą importowane wyłącznie odpowiednie biblioteki `netstandard2.1` z zależnościami; bibliotek `net10.0` nie kopiujemy do Assets. Biblioteka Transport ma zależności JSON, dlatego import pojedynczego DLL nie stanowi kompletnej integracji. Ich zgodność w Editorze i IL2CPP wymaga osobnego sprawdzenia. [Profile .NET w Unity](https://docs.unity3d.com/6000.6/Documentation/Manual/dotnet-profile-support.html).

## 5. GitHub

Repozytorium: [Mr444-dev/OmniTwin](https://github.com/Mr444-dev/OmniTwin), prywatne. W kolejnych etapach zapisujemy i wysyłamy sprawdzone zmiany do tego samego repozytorium. Synchronizacja następuje przy pracy nad projektem; nie ma osobnego procesu obserwującego wszystkie zmiany na dysku.

Po utworzeniu projektu zamknij edytor albo poczekaj na koniec importu i zapisz scenę. Git powinien objąć `Assets` wraz z `.meta`, `Packages/manifest.json`, `Packages/packages-lock.json` i `ProjectSettings`. `.gitignore` wyklucza cache, logi, lokalne ustawienia i buildy. Nie usuwaj plików `.meta`; utrzymują powiązania assetów.

Po utworzeniu projektu mogę dodać właściwy odbiornik strumienia i scenę, sprawdzić je w edytorze, a następnie wysłać ten etap na GitHub. Backendowe CI już ma konfigurację w `.github/workflows/verify.yml`; testy Unity wymagają osobnej integracji z edytorem i licencją.
