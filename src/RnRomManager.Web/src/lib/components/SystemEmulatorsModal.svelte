<script lang="ts">
	import type { SystemInfo } from '../api/types';
	import { emulatorGroups, linkHost } from '../systemEmulators';
	import ConfirmModal from './ConfirmModal.svelte';
	import Icon from './Icon.svelte';

	/** Systems tab B7/B8: links to the known emulators for a system, grouped by OS. Links only; nothing is hosted. */
	interface Props {
		open: boolean;
		system: SystemInfo;
	}

	let { open = $bindable(), system }: Props = $props();

	const groups = $derived(emulatorGroups(system.id));
</script>

<ConfirmModal bind:open title="{system.displayName} emulators" size="lg">
	{#if groups.length === 0}
		<div class="alert alert-info">There are no known emulators for {system.displayName}.</div>
	{:else}
		<div class="row row-cols-1 row-cols-md-2 g-3">
			{#each groups as group (group.os)}
				<div class="col">
					<h3 class="h6"><Icon name={group.icon} /> {group.label}</h3>
					<ul class="list-group" aria-label={group.label}>
						{#each group.emulators as emulator (emulator.name)}
							<li class="list-group-item">
								<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- external project sites -->
								<a href={emulator.url} target="_blank" rel="noopener noreferrer"
									>{emulator.name}
									<Icon name="external" class="small" /><span class="visually-hidden">
										(opens in a new tab)</span
									></a
								>
								{#if emulator.note}<div class="small text-body-secondary">{emulator.note}</div>{/if}
								<div class="small text-body-secondary">{linkHost(emulator.url)}</div>
							</li>
						{/each}
					</ul>
				</div>
			{/each}
		</div>
	{/if}
	<p class="small text-body-secondary mt-3 mb-0">
		Links go to each project's own site. RnRomManager doesn't host or check these downloads.
	</p>
</ConfirmModal>
