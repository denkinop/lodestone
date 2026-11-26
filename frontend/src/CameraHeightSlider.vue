<script setup lang="ts">
const height = defineModel<number>({ required: true })
</script>

<template>
  <input class="camera-height-slider" type="range" min="-1" max="1" step="0.01"
    :value="height" aria-label="摄像机高度" aria-orientation="vertical" title="摄像机高度"
    @input="height = ($event.currentTarget as HTMLInputElement).valueAsNumber">
</template>

<style scoped>
.camera-height-slider {
  display: block;
  width: 44px;
  margin: 0;
  padding: 0;
  border: 0;
  appearance: none;
  background: transparent;
  writing-mode: vertical-lr;
  direction: rtl;
  touch-action: none;
  cursor: ns-resize;
}
.camera-height-slider::-webkit-slider-runnable-track {
  width: 6px;
  border-radius: 999px;
  background: #f2f1ee;
  box-shadow: 0 0 0 1px #0002;
}
.camera-height-slider::-webkit-slider-thumb {
  appearance: none;
  width: 16px;
  height: 16px;
  margin-block-start: -5px;
  border-radius: 50%;
  background: var(--blue);
}
.camera-height-slider::-moz-range-track {
  width: 6px;
  height: 100%;
  border-radius: 999px;
  background: #f2f1ee;
  box-shadow: 0 0 0 1px #0002;
}
.camera-height-slider::-moz-range-thumb {
  width: 16px;
  height: 16px;
  border: 0;
  border-radius: 50%;
  background: var(--blue);
}
</style>
