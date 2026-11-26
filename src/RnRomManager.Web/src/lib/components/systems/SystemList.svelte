<script lang="ts">
	import SearchInput from '#lib/components/forms/SearchInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import SortHeader from '#lib/components/common/SortHeader.svelte';
	import { debounced } from '#lib/debounce.js';
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import type { SystemInfo } from '#lib/types/index.js';
	import { defaultDir, maxFind, systemsSearch } from '#lib/discovery.js';
	import type { GroupSort, SortDir } from '#lib/enums/index.js';
	import { formatCount } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import GroupLogo from '#lib/components/discovery/GroupLogo.svelte';

	/**
	 * Systems tab B3: every supported system (0 games included) in a sortable table, each name a link selecting it, with
	 * its logo (B18) and, for Admins, a Change logo button (B19).
	 */
	interface Props {
		systems: SystemInfo[];
		selected: string | null;
		/** B19: open the logo modal for a system. */
		onedit?: (system: SystemInfo) => void;
		/** B16: the URL's search text, absent = none. */
		find?: string;
		/** B16: a new search (trimmed, '' for none); the page writes it to the URL. */
		onfind?: (find: string) => void;
		/** G5: the URL's sort and direction, absent = Name / the column's default. */
		sort?: GroupSort;
		dir?: SortDir;
		/** G4: a header click; the page writes the sort to the URL. */
		onsort?: (sort: GroupSort, dir: SortDir) => void;
		/** G7: Clear; the page drops `find`, `sort` and `dir` from the URL. */
		onclear?: () => void;
	}

	let { systems, selected, onedit, find, onfind, sort, dir, onsort, onclear }: Props = $props();

	const sortKey = $derived<GroupSort>(sort ?? 'name');
	const direction = $derived<SortDir>(dir ?? defaultDir(sortKey));
	const columns: readonly { sort: GroupSort; label: string; numeric: boolean }[] = [
		{ sort: 'name', label: 'Name', numeric: false },
		{ sort: 'games', label: 'Games', numeric: true }
	];
	const byName = (a: SystemInfo, b: SystemInfo) =>
		a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' });

	/** B16: a client-side filter by display name or id (trimmed, case-insensitive contains); G4: sorted in the browser. */
	const shown = $derived.by(() => {
		const needle = find?.toLowerCase();
		const list = needle
			? systems.filter(
					(s) => s.displayName.toLowerCase().includes(needle) || s.id.toLowerCase().includes(needle)
				)
			: [...systems];
		const sign = direction === 'asc' ? 1 : -1;
		// Games ties go by name ascending in either direction.
		return list.sort((a, b) =>
			sortKey === 'games' ? sign * (a.gameCount - b.gameCount) || byName(a, b) : sign * byName(a, b)
		);
	});
	const admin = $derived(auth.isAdmin && !!onedit);

	function sortBy(key: GroupSort): void {
		onsort?.(key, key === sortKey ? (direction === 'asc' ? 'desc' : 'asc') : defaultDir(key));
	}

	let findText = $state(untrack(() => find ?? ''));
	const onFindInput = debounced(() => {
		if (findText.trim() !== (find ?? '')) onfind?.(findText.trim());
	}, 300);
	/** G7: cancels a pending search and empties the box; the page clears the URL. */
	function clear(): void {
		onFindInput.cancel();
		findText = '';
		onclear?.();
	}
	// A pending search is dropped on unmount.
	$effect(() => () => onFindInput.cancel());
	// URL → box (Back/Forward).
	$effect(() => {
		const current = find ?? '';
		untrack(() => {
			if (findText.trim() !== current) findText = current;
		});
	});
</script>

<div class="d-flex gap-2 mb-2">
	<label class="visually-hidden" for="find-systems">Search systems</label>
	<SearchInput
		size="sm"
		class="flex-grow-1"
		id="find-systems"
		placeholder="Search systems"
		maxlength={maxFind}
		bind:value={findText}
		oninput={onFindInput}
	/>
	{#if find || sortKey !== 'name' || direction !== 'asc'}
		<Button variant="secondary" outline size="sm" class="text-nowrap" icon="clear" onclick={clear}
			>Clear</Button
		>
	{/if}
</div>

{#if find && shown.length === 0}
	<EmptyState message="No systems match “{find}”." />
{/if}
<div class="card" hidden={shown.length === 0}>
	<div class="table-responsive">
		<table class="table table-sm table-striped table-hover align-middle mb-0" aria-label="Systems">
			<thead class="table-dark">
				<tr>
					<SortHeader
						columns={columns.map((c) => ({
							...c,
							class: c.numeric ? 'text-end text-nowrap' : undefined
						}))}
						sort={sortKey}
						dir={direction}
						onsort={sortBy}
					/>
					{#if admin}
						<th scope="col"><span class="visually-hidden">Actions</span></th>
					{/if}
				</tr>
			</thead>
			<tbody>
				{#each shown as system (system.id)}
					<tr class:table-primary={system.id === selected}>
						<td class="w-100 text-truncate" style="max-width: 0">
							<a
								class="text-decoration-none d-flex align-items-center gap-2 min-w-0"
								class:text-reset={system.id === selected}
								aria-current={system.id === selected ? 'true' : undefined}
								href="{resolve('/collections')}{systemsSearch({
									system: system.id,
									gamePage: 1,
									find,
									sort,
									dir
								})}"
							>
								<GroupLogo
									kind="systems"
									item={{ key: system.id, name: system.displayName, logoUrl: system.logoUrl }}
								/>
								<span class="text-truncate" title={system.displayName}>{system.displayName}</span>
							</a>
						</td>
						<td class="text-end text-nowrap">{formatCount(system.gameCount)}</td>
						{#if admin}
							<td>
								<Button
									icon="edit"
									class="btn btn-link btn-sm p-0"
									aria-label="Change logo for {system.displayName}"
									title="Change logo"
									onclick={() => onedit?.(system)}
								/>
							</td>
						{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
</div>
