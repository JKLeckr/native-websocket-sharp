# justfile

set windows-shell := ["powershell.exe", "-NoLogo", "-Command"]

proj_dir := 'WebSocketSharp'
#proj_name := 'websocket-sharp'
solution_name := 'native-websocket-sharp.slnx'
unit_test_dir := 'WebSocketSharp.Tests'
#unit_test_proj_name := 'WebSocketSharp.Tests'
test_server_dir := 'wsmini'
#test_server_name := 'WSMini'

default:
    @just --list

[working-directory: 'nativews']
setup-native:
    cargo fetch

setup:
    @just setup-native
    dotnet restore ./{{ solution_name }} -m:1

[working-directory: 'nativews']
build-native config='Debug':
    cargo build {{ if config == 'Release' { '--release' } else { '' } }}

build config='Debug' framework='':
    @just build-native {{ config }}
    dotnet build {{ proj_dir }} -c {{ config }} {{ if framework != '' { '-f ' + framework } else { '' } }}

build-tests config='Debug' framework='':
    @just build-native {{ config }}
    dotnet build {{ unit_test_dir }} -c {{ config }} {{ if framework != '' { '-f ' + framework } else { '' } }}

build-all config='Debug':
    @just build-native {{ config }}
    dotnet build ./{{ solution_name }} -c {{ config }} -m:1

[working-directory: 'nativews']
clean-native:
    cargo clean

clean:
    @just clean-native
    dotnet clean ./{{ solution_name }}

run-test-server:
    dotnet run --project {{ test_server_dir }} --framework net8.0

test:
    dotnet test {{ unit_test_dir }} -c Debug -f net8.0

test-unit:
    @just test

test-all:
    @just test
