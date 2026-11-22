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
  home: 'm3 10 9-7 9 7m-16-1v11h5v-6h4v6h5V9',
  sidebar: 'M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Zm5 0v16M6 8h1m-1 4h1',
  calendar: 'M5 5h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2Zm2-2v4m10-4v4M3 10h18m-14 4h1m3 0h1m3 0h1m-9 3h1m3 0h1',
  news: 'M6 4h14v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8h2m0-4v14a2 2 0 0 0 2 2m2-12h6m-6 4h6m-6 4h4',
  left: 'm14 6-6 6 6 6',
  right: 'm10 6 6 6-6 6',
  external: 'M14 4h6v6m0-6-8 8m6 1v6a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h6',
  sun: 'M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5m-2 7a5 5 0 1 1-10 0 5 5 0 0 1 10 0Z',
  weather: 'M10 4V2M4.4 5.4 3 4M17 4l-1.4 1.4M3 11H1m5.2-1a4 4 0 0 1 7.5-1.6M7.5 19a3.5 3.5 0 1 1 .8-6.9 5 5 0 0 1 9.5-.7 3.8 3.8 0 1 1 .4 7.6Z',
  moon: 'M20.5 15A8.8 8.8 0 0 1 9 3.5 9 9 0 1 0 20.5 15Z',
}
</script>

<template>
  <svg :width="size || 20" :height="size || 20" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
    <path :d="paths[name]" />
  </svg>
</template>
