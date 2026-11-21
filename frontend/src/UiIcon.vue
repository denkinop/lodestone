<script setup lang="ts">
defineProps<{ name: string; size?: number }>()

const paths: Record<string, string> = {
  crystal: 'M12 2 20 12 12 22 4 12 12 2Zm0 0v20M4 12l8 4 8-4M4 12l8-4 8 4',
  user: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M16 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z',
  settings: 'm9.6 3-.5 2.3-1.9 1.1L5 5.7l-2.4 4.2 1.7 1.6v2.1l-1.7 1.6L5 19.4l2.2-.7 1.9 1.1.5 2.2h4.8l.5-2.2 1.9-1.1 2.2.7 2.4-4.2-1.7-1.6v-2.1l1.7-1.6L19 5.7l-2.2.7-1.9-1.1-.5-2.3h-4.8ZM15 12.5a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  cube: 'M12 2 3 7v10l9 5 9-5V7l-9-5ZM3 7l9 5 9-5m-9 5v10',
  download: 'M12 3v12m-5-5 5 5 5-5M5 16v4h14v-4',
  focus: 'M8 4H4v4m12-4h4v4M4 16v4h4m12-4v4h-4M9 12h6m-3-3v6',
  location: 'M20 10c0 5-8 11-8 11S4 15 4 10a8 8 0 1 1 16 0Zm-5 0a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  close: 'm6 6 12 12M18 6 6 18',
  shirt: 'm8 3 4 2 4-2 6 4-3 5-2-1v10H7V11l-2 1-3-5 6-4Z',
  book: 'M4 5h6a2 2 0 0 1 2 2v14a3 3 0 0 0-3-3H4V5Zm16 0h-6a2 2 0 0 0-2 2m0 14a3 3 0 0 1 3-3h5V5',
  chart: 'M4 20h16M6 17v-4m6 4V9m6 8V5',
  check: 'm5 12 4 4 10-10',
  swap: 'M4 8h15m-4-4 4 4-4 4M20 16H5m4-4-4 4 4 4',
}
</script>

<template>
  <svg :width="size || 20" :height="size || 20" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
    <path :d="paths[name]" />
  </svg>
</template>
