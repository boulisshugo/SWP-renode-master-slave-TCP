# Bare-metal build for the NUCLEO-H533RE SWP demo.
#   make            -> build/swp-demo.elf
#   make clean

CROSS   ?= arm-none-eabi-
CC      := $(CROSS)gcc
OBJCOPY := $(CROSS)objcopy
SIZE    := $(CROSS)size

BUILD   := build
TARGET  := $(BUILD)/swp-demo.elf

CFLAGS  := -mcpu=cortex-m33 -mthumb -mfloat-abi=soft \
           -O2 -g3 -std=c11 -Wall -Wextra \
           -ffunction-sections -fdata-sections \
           -ffreestanding -nostdlib

LDFLAGS := -T link.ld -Wl,--gc-sections -Wl,-Map=$(BUILD)/swp-demo.map \
           -nostdlib -nostartfiles

SRCS    := src/startup.c src/main.c

all: $(TARGET)

$(TARGET): $(SRCS) link.ld | $(BUILD)
	$(CC) $(CFLAGS) $(LDFLAGS) $(SRCS) -o $@
	$(OBJCOPY) -O binary $@ $(BUILD)/swp-demo.bin
	$(SIZE) $@

$(BUILD):
	mkdir -p $@

clean:
	rm -rf $(BUILD)

.PHONY: all clean
