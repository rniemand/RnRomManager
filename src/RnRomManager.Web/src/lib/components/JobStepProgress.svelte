<script lang="ts">
	import type { JobStep } from '../api/types';
	import { formatStep, stepPercent } from '../jobs';

	interface Props {
		/** The running job's current-file step; nothing renders without one. */
		step: JobStep | null | undefined;
	}

	let { step }: Props = $props();
</script>

<!-- Large ROM import progress B7: never inside a live region, since it changes on every poll. -->
{#if step}
	<div class="job-step mt-2">
		<div
			class="progress mb-1"
			style="height: 0.5rem"
			role="progressbar"
			aria-label="Current file progress"
			aria-valuenow={stepPercent(step)}
			aria-valuemin="0"
			aria-valuemax="100"
		>
			<div class="progress-bar bg-info" style="width: {stepPercent(step)}%"></div>
		</div>
		<div class="small text-body-secondary text-break">{formatStep(step)}</div>
	</div>
{/if}
