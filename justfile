# justfile

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

proj_dir := 'WebSocketSharp'
#proj_name := 'websocket-sharp'
solution_name := 'native-websocket-sharp.slnx'
unit_test_dir := 'WebSocketSharp.Tests'
#unit_test_proj_name := 'WebSocketSharp.Tests'
test_server_dir := 'WSMini'
#test_server_name := 'WSMini'
dotnet_flags :=  '-m:1'

default:
    @just --list

[working-directory: 'nativews']
_setup-native:
    cargo fetch

setup:
    @just _setup-native
    dotnet restore ./{{ solution_name }} -m:1

build config='Debug' framework='' flags=dotnet_flags:
    dotnet build {{ proj_dir }} -c {{ config }} {{ if framework != '' { '-f ' + framework } else { '' } }} {{ flags }}

build-tests config='Debug' framework='' flags=dotnet_flags:
    dotnet build {{ unit_test_dir }} -c {{ config }} {{ if framework != '' { '-f ' + framework } else { '' } }} {{ flags }}

build-all config='Debug' flags=dotnet_flags:
    dotnet build ./{{ solution_name }} -c {{ config }} {{ flags }}

[working-directory: 'nativews']
_clean-native:
    cargo clean

clean:
    @just _clean-native
    dotnet clean ./{{ solution_name }} -m:1

run-test-server flags=dotnet_flags:
    dotnet run --project {{ test_server_dir }} --framework net10.0 {{ flags }}

test flags=dotnet_flags:
    dotnet test --project {{ unit_test_dir }} -c Debug -f net10.0 {{ flags }}

test-unit:
    @just test

test-all:
    @just test
