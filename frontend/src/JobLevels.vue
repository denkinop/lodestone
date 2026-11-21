<script setup lang="ts">
import { jobGroups, jobIcon, jobName, type JobId, type JobProgress } from './jobs'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ progress?: JobProgress }>()

function label(id: JobId) {
  const level = props.progress?.levels[id]
  return jobName(id) + ' · ' + (level === 0 ? '未解锁' : level == null ? '-' : level + ' 级')
}
</script>

<template>
  <section class="paper job-panel" aria-labelledby="jobs-title">
    <div class="panel-title">
      <span class="section-icon"><UiIcon name="chart" :size="18" /></span>
      <h2 id="jobs-title">职业等级</h2>
    </div>
    <div v-for="group in jobGroups" :key="group.label" class="job-row">
      <h3>{{ group.label }}</h3>
      <ul class="job-list">
        <li v-for="[id] in group.jobs" :key="id" class="job-token"
          :class="{ 'current-job': id === progress?.current }" :title="label(id)">
          <img :src="jobIcon(id)" :alt="jobName(id)" width="26" height="26">
          <span>{{ progress?.levels[id] || '-' }}</span>
        </li>
      </ul>
    </div>
  </section>
</template>
