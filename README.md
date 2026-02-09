# Printserver (RockPi, Container, .NET)

Dieses Repository enthaelt ein .NET-API-Grundgeruest fuer einen Printserver, der in einem Container laeuft (z.B. auf einem RockPi). Es ist **nicht** fertig wie Repetier Server, sondern bietet ein API zum Einreichen und Verwalten von Jobs, eine einfache Queue sowie Webcam-Endpunkte (Snapshot/Stream). Du kannst darauf aufbauen (z.B. G-Code-Streaming ueber Serial, Authentifizierung, UI).

> Hinweis: Das Projekt zielt auf **.NET 10 (Preview/Nightly)**. Das bedeutet, dass lokal und im Container eine entsprechende .NET-10-SDK/Runtime benoetigt wird.

## Funktionen
- `POST /jobs` erstellt einen Job (Name + G-Code als Text)
- `POST /jobs/upload` laedt eine G-Code-Datei via `multipart/form-data` hoch
- `GET /jobs` listet Jobs
- `GET /jobs/{id}` gibt einen Job zurueck
- `GET /jobs/{id}/gcode` gibt den G-Code zurueck
- `POST /jobs/{id}/start` setzt Status auf `Printing`
- `POST /jobs/{id}/complete` setzt Status auf `Completed`
- `POST /jobs/{id}/cancel` setzt Status auf `Canceled`
- `DELETE /jobs/{id}` loescht einen Job
- `GET /queue/next` liefert den naechsten Job aus der Queue
- `GET /webcam/snapshot` liefert ein Snapshot-JPEG
- `GET /webcam/stream` proxyt einen MJPEG-Stream

## Lokaler Start
```bash
cd src/Printserver
DOTNET_ENVIRONMENT=Development dotnet run
```

## Container Build/Run
```bash
docker build -t printserver .
docker run --rm -p 8080:8080 \
  -e PrintServer__DataDirectory=/data \
  -e Webcam__SnapshotPath=/data/webcam.jpg \
  -e Webcam__MjpegUrl=http://rockpi.local:8081/stream \
  -v $(pwd)/data:/data \
  printserver
```

## Konfiguration
Die wichtigsten Einstellungen sind als Environment-Variablen gesetzt (oder ueber `appsettings.json`).

| Setting | Beschreibung | Beispiel |
| --- | --- | --- |
| `PrintServer__DataDirectory` | Speicherort fuer `jobs.json` | `/data` |
| `Webcam__SnapshotPath` | Pfad zu einem JPEG-Snapshot | `/data/webcam.jpg` |
| `Webcam__MjpegUrl` | MJPEG-Stream-URL, die durchgereicht wird | `http://host:port/stream` |

## Beispiel-Request
```bash
curl -X POST http://localhost:8080/jobs \
  -H "Content-Type: application/json" \
  -d '{"name":"Testdruck","content":"G1 X10 Y10"}'
```

```bash
curl -X POST http://localhost:8080/jobs/upload \
  -F "name=MeinJob" \
  -F "file=@example.gcode"
```

## Naechste Schritte
- Serielle Kommunikation (z.B. via `/dev/ttyUSB0`) und G-Code Streaming.
- Echte Webcam-Anbindung ueber libcamera/v4l2 (Snapshot-Erzeugung automatisieren).
- Authentifizierung und Web-UI.
