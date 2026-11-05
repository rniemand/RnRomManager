<script lang="ts">
	import { theme, type ThemeChoice } from '../theme.svelte';
	import Icon from './Icon.svelte';

	const options: { value: ThemeChoice; label: string }[] = [
		{ value: 'light', label: 'Light' },
		{ value: 'dark', label: 'Dark' },
		{ value: 'auto', label: 'Auto' }
	];
	const current = $derived(options.find((o) => o.value === theme.choice)!);
</script>

<div class="dropdown">
	<button
		type="button"
		class="btn btn-link nav-link dropdown-toggle"
		data-bs-toggle="dropdown"
		aria-expanded="false"
		aria-label="Theme: {current.label}"
	>
		<Icon name="theme-{current.value}" />
	</button>
	<ul class="dropdown-menu dropdown-menu-end">
		{#each options as option (option.value)}
			<li>
				<button
					type="button"
					class="dropdown-item d-flex align-items-center gap-2"
					class:active={theme.choice === option.value}
					aria-pressed={theme.choice === option.value}
					onclick={() => theme.set(option.value)}
				>
					<Icon name="theme-{option.value}" />
					{option.label}
				</button>
			</li>
		{/each}
	</ul>
</div>
