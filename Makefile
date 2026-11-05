# sts2-dualscreen build.
#
#   make setup     install the toolchain (Homebrew + Android SDK + .NET 9)
#   make runtime   take the engine runtime from the Sts2MobileLauncher release APK
#   make compat    rebuild the game's compat pack target from source, with the
#                  pool patches (COMPAT_REF=<dir with sts2.dll> once; saved)
#   make managed   build the entry shim and the dual-screen layer (C#)
#   make apk       build dist/sts2-dualscreen-<version>.apk (for playing);
#                  COMPAT=release keeps the released compat pack as is
#   make install   adb install it
#   make dev-apk   build dist/sts2-dualscreen-<version>-dev.apk (debuggable: run-as works)
#   make install-dev   adb install that
#   make dev       dev-apk + install-dev + launch + follow the log
#   make test      unit tests
#   make logcat    follow the app's log

SHELL := /bin/bash
JAVA_HOME ?= $(shell brew --prefix openjdk@21 2>/dev/null)/libexec/openjdk.jdk/Contents/Home
ANDROID_HOME ?= $(HOME)/Library/Android/sdk
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)
export JAVA_HOME ANDROID_HOME
export DOTNET_CLI_TELEMETRY_OPTOUT := 1

VERSION := $(shell sed -n 's/^sts2ds\.version=//p' launcher/gradle.properties)
APK := launcher/app/build/outputs/apk/release/sts2-dualscreen-$(VERSION)-release.apk
DIST := dist/sts2-dualscreen-$(VERSION).apk
ASSETS := launcher/app/src/main/assets
RUNTIME_STAMP := $(ASSETS)/runtime_manifest.json
# The patched compat pack: rebuilt when the runtime or a patch is newer.
COMPAT_STAMP := .cache/runtime/compat/stamp
COMPAT ?= patched
COMPAT_DEP := $(if $(filter release,$(COMPAT)),,$(COMPAT_STAMP))
PACKAGE := dev.sts2ds
RUNTIME_TAG ?= v0.1.11
GODOT_AAR := launcher/app/libs/godot-lib.template_release.aar
GODOT_AAR_STOCK := .cache/runtime/$(RUNTIME_TAG)/godot-4.5.1.stable.aar
GODOT_INPUT := tools/runtime/godot-input
ANDROID_JAR := $(ANDROID_HOME)/platforms/android-35/android.jar

.PHONY: setup runtime compat managed apk install dev-apk install-dev dev test test-runtime clean logcat

setup:
	./tools/setup-mac.sh

$(RUNTIME_STAMP):
	./tools/runtime/make-runtime.sh

runtime:
	./tools/runtime/make-runtime.sh

compat: $(RUNTIME_STAMP)
	./tools/runtime/make-compat.sh
	touch $(COMPAT_STAMP)

$(COMPAT_STAMP): $(RUNTIME_STAMP) tools/runtime/make-compat.sh $(wildcard tools/runtime/compat-patches/*.patch)
	./tools/runtime/make-compat.sh
	touch $@

managed: $(RUNTIME_STAMP)
	$(DOTNET) build managed/STS2DsEntry/STS2DsEntry.csproj -c Release -nologo -v q
	mkdir -p $(ASSETS)/dualscreen
	# File name the engine opens; assembly name STS2DsEntry (see the .csproj).
	cp managed/STS2DsEntry/bin/Release/net9.0/STS2DsEntry.dll $(ASSETS)/dualscreen/STS2Mobile.dll
	cp managed/STS2DualScreen/bin/Release/net9.0/STS2DualScreen.dll $(ASSETS)/dualscreen/

launcher/local.properties:
	echo "sdk.dir=$(ANDROID_HOME)" > $@

apk: managed $(COMPAT_DEP) launcher/local.properties
	cd launcher && ./gradlew --no-daemon -q assembleRelease
	mkdir -p dist
	cp $(APK) $(DIST)
	@echo "Built $(DIST)"

install:
	adb install -r $(DIST)

DEV_APK := launcher/app/build/outputs/apk/devel/sts2-dualscreen-$(VERSION)-devel.apk
DEV_DIST := dist/sts2-dualscreen-$(VERSION)-dev.apk

dev-apk: managed $(COMPAT_DEP) launcher/local.properties
	cd launcher && ./gradlew --no-daemon -q assembleDevel
	mkdir -p dist
	cp $(DEV_APK) $(DEV_DIST)
	@echo "Built $(DEV_DIST)"

install-dev:
	adb install -r $(DEV_DIST)

logcat:
	adb logcat -v time -s STS2DualScreen Sts2Launcher GameActivity godot FMOD AndroidRuntime DEBUG

dev: dev-apk install-dev
	adb shell am start -n $(PACKAGE)/dev.sts2ds.launcher.LauncherActivity
	$(MAKE) logcat

test: launcher/local.properties test-runtime
	cd launcher && ./gradlew --no-daemon -q testReleaseUnitTest

# The staged Godot AAR carries the input repair, and the repair passes
# upstream's regression on the stock AAR.
test-runtime: $(RUNTIME_STAMP)
	python3 $(GODOT_INPUT)/patch-godot-input-pool.py --check $(GODOT_AAR)
	python3 $(GODOT_INPUT)/test-godot-input-source.py --javac $(JAVA_HOME)/bin/javac \
	  --android-jar $(ANDROID_JAR) $(GODOT_AAR_STOCK)

clean:
	cd launcher && ./gradlew --no-daemon -q clean
	rm -rf dist managed/*/bin managed/*/obj $(ASSETS)/dualscreen
