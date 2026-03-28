# native-websocket-sharp

This is a partially implemented wrapper for the original websocket-sharp that instead uses a native implementation written in Rust. This is to ensure older .NET Framework versions can use later TLS versions and compression.

This library is mainly designed for [Archipelago.MultiClient.NET](https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net) and will focus on implementation of the parts that are used in its [websocket-sharp helper](https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net/blob/main/Archipelago.MultiClient.Net/Helpers/ArchipelagoSocketHelper_websocket-sharp.cs).

## How to build:

### Prerequisites:
- [.NET SDK](https://dotnet.microsoft.com/en-us/download) 10 or greater. You need the `dotnet` program in your path.

### Build
#### Use just to build on the websocket-sharp project:
```sh
just setup
just build
```

## How to test:
```sh
just test
```
