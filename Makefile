.PHONY: build build-all build-client build-server build-win build-linux build-osx channel-binary channel-source

build: build-all

build-all:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target all

build-client:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target client

build-server:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target server

build-win:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target windows

build-linux:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target linux

build-osx:
	powershell -ExecutionPolicy Bypass -File ./build-assembly.ps1 -Configuration Release -Target osx

channel-binary:
	powershell -ExecutionPolicy Bypass -File ./switch-channel.ps1 -Channel binary

channel-source:
	powershell -ExecutionPolicy Bypass -File ./switch-channel.ps1 -Channel source
