# com-ports

Tool console Windows in C# per enumerare:

- tutte le porte seriali COM presenti sul sistema;
- i device FTDI collegati e associati a una COM;
- il seriale FTDI ricavato dall'instance ID del device;
- una descrizione leggibile del device, quando disponibile.

Il tool supporta tre modalita' di output:

- lista semplice human-readable;
- tabelle formattate human-readable;
- JSON.

L'output JSON ha il formato:

```json
{
  "coms": ["COM7", "COM57"],
  "ftdis": [
    {
      "com": "COM7",
      "serial": "DPB0J35R",
      "description": "USB Serial Converter"
    }
  ]
}
```

## Obiettivo

Il progetto nasce per avere un eseguibile molto piccolo, con il minor numero possibile di dipendenze e senza librerie terze per FTDI.

In particolare:

- nessun package NuGet esterno;
- solo BCL .NET e chiamate Win32 native;
- nessuna dipendenza da `FTD2XX.dll`, driver SDK proprietari o wrapper esterni;
- output JSON adatto a essere consumato da altri script o applicativi.

## Struttura del repository

- [Program.cs](/home/paul/repos/com-ports/Program.cs): implementazione completa del tool.
- [ComPortsTool.csproj](/home/paul/repos/com-ports/ComPortsTool.csproj): progetto .NET.
- [com-ports.sln](/home/paul/repos/com-ports/com-ports.sln): solution Visual Studio.
- [.gitignore](/home/paul/repos/com-ports/.gitignore): esclusioni build/IDE.
- [scripts/release-github.sh](/home/paul/repos/com-ports/scripts/release-github.sh): script Linux per bump versione, tag, push e creazione release GitHub.

## Come funziona

Il tool usa due livelli di enumerazione.

### 1. Enumerazione delle COM

Le porte COM vengono enumerate tramite `SetupAPI`, interrogando l'interfaccia di classe:

- `GUID_DEVINTERFACE_COMPORT`

Per ogni device COM presente il tool legge:

- `PortName` dal registro del device;
- `FriendlyName`;
- `Device Description`;
- `Device Instance ID`.

Le COM vengono poi:

- normalizzate in maiuscolo;
- deduplicate;
- ordinate per numero (`COM7` prima di `COM57`).

Nota: la richiesta iniziale menzionava `SerialPort.GetPortNames()`. In questo repository ho implementato l'enumerazione via `SetupAPI` per mantenere il progetto compilabile anche dall'ambiente WSL offline usato durante lo sviluppo, senza introdurre il package `System.IO.Ports` da NuGet. Su Windows il risultato atteso resta l'elenco delle COM realmente presenti.

### 2. Riconoscimento dei device FTDI

Per ogni COM enumerata, il tool risale l'albero PnP tramite `cfgmgr32`:

- `CM_Locate_DevNode`
- `CM_Get_Parent`
- `CM_Get_Device_ID`

Quando trova un ancestor con instance ID compatibile FTDI:

- `FTDIBUS\\VID_0403+PID_...`
- oppure `USB\\VID_0403&PID_...`

lo considera il device FTDI associato alla COM.

### 3. Estrazione del seriale FTDI

Il seriale viene derivato dall'instance ID FTDI:

- per `FTDIBUS\...` il token seriale viene estratto dalla parte centrale dell'ID;
- se il token termina con suffissi canale `A`, `B`, `C`, `D`, questi vengono rimossi;
- per alcuni device esposti come `USB\VID_0403&PID_...\SERIALE`, il seriale viene preso direttamente dal secondo segmento.

Questo approccio funziona bene per la maggior parte dei bridge FTDI USB/seriali standard.

### 4. Descrizione del device

Il campo `description` viene valorizzato tentando, in ordine:

- device description dell'ancestor FTDI;
- friendly name dell'ancestor FTDI;
- friendly name della COM ripulito dal suffisso `(COMxx)`;
- device description della COM;
- friendly name della COM.

In pratica, il valore dipende da cosa Windows espone nel PnP tree e dal driver installato. Per molti FTDI il risultato tipico e':

```json
"USB Serial Converter"
```

## Modalita' output

### Lista semplice

E' la modalita' di default:

```text
COM ports:
  COM7
  COM57

FTDI devices:
  COM7 | DPB0J35R | USB Serial Converter
  COM57 | AM003XEP | USB Serial Converter
```

### Tabelle

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --table
```

Output indicativo:

```text
COM ports
COM
-----
COM7
COM57

FTDI devices
COM    SERIAL    DESCRIPTION
-----  --------  --------------------
COM7   DPB0J35R  USB Serial Converter
COM57  AM003XEP  USB Serial Converter
```

### JSON

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --json
```

Il programma scrive su `stdout` un JSON con due campi:

### `coms`

Array di stringhe:

```json
["COM3", "COM7", "COM57"]
```

### `ftdis`

Array di oggetti:

- `com`: nome della COM associata;
- `serial`: seriale FTDI;
- `description`: descrizione leggibile, se disponibile.

Esempio:

```json
[
  {
    "com": "COM7",
    "serial": "DPB0J35R",
    "description": "USB Serial Converter"
  },
  {
    "com": "COM57",
    "serial": "AM003XEP",
    "description": "USB Serial Converter"
  }
]
```

## Requisiti

- Windows per l'esecuzione reale del tool;
- .NET 8 SDK per la build;
- driver FTDI gia' installati su Windows, se si vuole ottenere mappatura FTDI -> COM.

Il progetto targetta:

```text
net8.0-windows
```

## Build

### Build da Windows

```bash
dotnet build
```

### Build da WSL

La build del progetto funziona anche da WSL:

```bash
dotnet build -p:RestoreIgnoreFailedSources=true
```

Poiche' il progetto non usa package esterni, non richiede restore NuGet aggiuntivo.

Output tipico:

```text
bin/Debug/net8.0-windows/com-ports.dll
```

## Esecuzione

### Versione del tool

Il binario espone anche la versione corrente:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --version
```

Oppure:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll -v
```

### Selezione formato output

Lista semplice, default:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll
```

Oppure in modo esplicito:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --list
```

JSON:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --json
```

Tabelle:

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll --table
```

### Esecuzione diretta su Windows

```powershell
dotnet .\bin\Debug\net8.0-windows\com-ports.dll
```

Oppure dopo publish:

```powershell
dotnet .\bin\Release\net8.0-windows\com-ports.dll
```

### Esecuzione da WSL usando il runtime Windows

Se il repository e' in WSL ma si vuole interrogare l'hardware collegato a Windows:

```bash
/mnt/c/Program\ Files/dotnet/dotnet.exe /home/paul/repos/com-ports/bin/Debug/net8.0-windows/com-ports.dll
```

Questo punto e' importante:

- il codice deve girare nel runtime Windows;
- eseguirlo con il `dotnet` Linux di WSL non puo' interrogare `setupapi.dll` e `cfgmgr32.dll` del sistema Windows.

## Verifica effettuata

Durante lo sviluppo il tool e' stato compilato in WSL ed eseguito tramite il runtime .NET di Windows. Sul sistema di test ha restituito:

```json
{"coms":["COM7","COM57"],"ftdis":[{"com":"COM7","serial":"DPB0J35R","description":"USB Serial Converter"},{"com":"COM57","serial":"AM003XEP","description":"USB Serial Converter"}]}
```

Questa verifica conferma:

- enumerazione COM funzionante;
- associazione COM -> FTDI funzionante;
- estrazione del seriale FTDI funzionante;
- produzione del JSON nel formato richiesto.

## Limiti noti

- Il tool riconosce solo device FTDI con VID/PID e pattern di instance ID compatibili con quelli gestiti.
- `description` dipende dalle proprieta' esposte dal driver e dal device manager di Windows; non esiste garanzia di ottenere una descrizione "commerciale" perfetta.
- Se un adattatore seriale non e' FTDI, apparira' in `coms` ma non in `ftdis`.
- Se un device FTDI e' presente ma la catena PnP non consente di risalire correttamente all'ancestor FTDI, il mapping puo' mancare.
- La publish del pacchetto di release e' framework-dependent: richiede `dotnet` sul sistema che lo esegue.

## Release GitHub

Il repository include uno script Linux per pubblicare una nuova release GitHub:

```bash
scripts/release-github.sh [patch|minor|major|current|X.Y.Z]
```

Se non viene passato alcun argomento, lo script esegue un bump `patch`.

Esempi:

```bash
scripts/release-github.sh
scripts/release-github.sh minor
scripts/release-github.sh current
scripts/release-github.sh 1.2.0
```

Lo script:

- fallisce se il working tree non e' pulito;
- legge la versione corrente da `ComPortsTool.csproj`;
- aggiorna `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`;
- crea commit `Release vX.Y.Z`;
- crea tag annotato `vX.Y.Z`;
- pusha branch corrente e tag su `origin`;
- esegue `dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true`;
- verifica che l'output pubblicato sia un solo file;
- carica sulla GitHub release un solo file `.exe`.

La modalita' `current` non incrementa la versione:

- usa la versione gia' presente in `ComPortsTool.csproj`;
- richiede che il tag `vX.Y.Z` esista gia';
- rigenera ZIP e GitHub release per quella versione.

E' utile se un run precedente ha gia' creato commit e tag ma la release e' fallita dopo il push.

Prerequisiti per lo script:

- remote `origin` gia' configurata;
- `gh` autenticato con permessi di release;
- `dotnet`, `python3`, `git` disponibili;
- accesso ai runtime pack richiesti da `dotnet publish -r win-x64`.

Artifact generato:

```text
artifacts/com-ports-vX.Y.Z-win-x64.exe
```

## Possibili estensioni

- aggiungere `vid` e `pid` nel JSON di output;
- aggiungere un flag `isFtdi` per ogni COM;
- aggiungere una modalita' `--pretty` per JSON indentato;
- aggiungere export su file;
- introdurre una variante Windows che usi esplicitamente `SerialPort.GetPortNames()` se l'ambiente di build dispone di `System.IO.Ports`.

## Licenza

Nessuna licenza definita nel repository al momento.
