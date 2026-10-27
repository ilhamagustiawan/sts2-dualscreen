# sts2-dualscreen build.
#
#   make setup     install the toolchain (Homebrew + Android SDK + .NET 9)
#   make runtime   take the engine runtime from the Sts2MobileLauncher release APK
#   make managed   build the entry shim and the dual-screen layer (C#)
#   make apk       build dist/sts2-dualscreen-<version>.apk
#   make install   adb install it
#   make dev       apk + install + launch + follow the log
#   make test      unit tests

SHELL := /bin/bash
JAVA_HOME ?= $(shell brew --prefix openjdk@21 2>/dev/null)/libexec/openjdk.jdk/Contents/Home
ANDROID_HOME ?= $(HOME)/Library/Android/sdk
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)
export JAVA_HOME ANDROID_HOME
export DOTNET_CLI_TELEMETRY_OPTOUT := 1

VERSION := $(shell cat VERSION)
APK := launcher/app/build/outputs/apk/release/sts2-dualscreen-$(VERSION)-release.apk
DIST := dist/sts2-dualscreen-$(VERSION).apk
ASSETS := launcher/app/src/main/assets
RUNTIME_STAMP := $(ASSETS)/runtime_manifest.json
PACKAGE := dev.sts2ds

.PHONY: setup runtime managed apk install dev test clean logcat

setup:
	./tools/setup-mac.sh

$(RUNTIME_STAMP):
	./tools/runtime/make-runtime.sh

runtime:
	./tools/runtime/make-runtime.sh

managed: $(RUNTIME_STAMP)
	$(DOTNET) build managed/STS2DsEntry/STS2DsEntry.csproj -c Release -nologo -v q
	mkdir -p $(ASSETS)/dualscreen
	cp managed/STS2DsEntry/bin/Release/net9.0/STS2Mobile.dll $(ASSETS)/dualscreen/
	cp managed/STS2DualScreen/bin/Release/net9.0/STS2DualScreen.dll $(ASSETS)/dualscreen/

launcher/local.properties:
	echo "sdk.dir=$(ANDROID_HOME)" > $@

apk: managed launcher/local.properties
	cd launcher && ./gradlew --no-daemon -q assembleRelease
	mkdir -p dist
	cp $(APK) $(DIST)
	@echo "Built $(DIST)"

install:
	adb install -r $(DIST)

logcat:
	adb logcat -v time -s STS2DualScreen Sts2Launcher GameActivity godot FMOD AndroidRuntime DEBUG

dev: apk install
	adb shell am start -n $(PACKAGE)/dev.sts2ds.launcher.LauncherActivity
	$(MAKE) logcat

test: launcher/local.properties
	cd launcher && ./gradlew --no-daemon -q testReleaseUnitTest

clean:
	cd launcher && ./gradlew --no-daemon -q clean
	rm -rf dist managed/*/bin managed/*/obj $(ASSETS)/dualscreen
