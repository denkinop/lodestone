<script setup lang="ts">
import { jobGroups, jobIcon, jobName, type JobId, type JobProgress } from './jobs'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ progress: JobProgress }>()

function label(id: JobId) {
  const level = props.progress.levels[id]
  return jobName(id) + ' · ' + (level === 0 ? '未解锁' : level == null ? '等级未获取' : level + ' 级')
}
</script>

<template>
  <section class="paper job-panel" aria-labelledby="jobs-title">
    <div class="panel-title">
      <span class="section-icon"><UiIcon name="chart" :size="18" /></span>
      <h2 id="jobs-title">职业等级</h2>
      <span class="panel-title-note">{{ progress.source === 'demo' ? '演示数据' : '国服社区' }}</span>
    </div>
    <div v-for="group in jobGroups" :key="group.label" class="job-row">
      <h3>{{ group.label }}</h3>
      <ul class="job-list">
        <li v-for="[id] in group.jobs" :key="id" class="job-token"
          :class="{ 'current-job': id === progress.current, 'community-job-token': progress.source === 'community' }" :title="label(id)">
          <span v-if="progress.source === 'community'">{{ jobName(id) }}</span>
          <img v-else :src="jobIcon(id)" :alt="jobName(id)" width="26" height="26">
          <span>{{ progress.source === 'community' && progress.levels[id] === 0 ? '未解锁' : progress.levels[id] || '—' }}</span>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.community-job-token { min-width: 5rem; width: auto; padding: 4px 8px; }
</style>
