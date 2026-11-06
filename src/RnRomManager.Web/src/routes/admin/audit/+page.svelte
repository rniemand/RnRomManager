<script lang="ts">
	import { onDestroy } from 'svelte';
	import { SvelteDate, SvelteURLSearchParams } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	interface AuditItem {
		id: number;
		occurredAt: string;
		action: string;
		system: string | null;
		gameId: string | null;
		user: string | null;
		path: string | null;
		targetPath: string | null;
		details: string | null;
	}
	const keys = ['action', 'system', 'user', 'scheduled', 'q', 'from', 'to', 'page'] as const;
	type Key = (typeof keys)[number];
	const params = $derived(page.url.searchParams);
	const currentPage = $derived(Math.max(1, Number(params.get('page')) || 1));
	const scheduled = $derived(params.get('scheduled') === 'true');
	let actions = $state<string[]>([]);
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<AuditItem> | null>(null);
	let error = $state<unknown>(null);
	let optionsError = $state<unknown>(null);
	let reload = $state(0);
	let userTimer: ReturnType<typeof setTimeout> | undefined;
	let searchTimer: ReturnType<typeof setTimeout> | undefined;
	function cancelTimers(): void {
		clearTimeout(userTimer);
		clearTimeout(searchTimer);
	}
	onDestroy(cancelTimers);

	function update(changes: Partial<Record<Key, string>>): void {
		const next = new SvelteURLSearchParams(params.toString());
		next.delete('page');
		for (const [key, value] of Object.entries(changes)) {
			if (value) next.set(key, value);
			else next.delete(key);
		}
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/admin/audit${next.size ? `?${next}` : ''}`, { replace: true, reset: false });
	}
	function clear(): void {
		cancelTimers();
		update(Object.fromEntries(keys.map((key) => [key, ''])));
	}
	function textInput(key: 'user' | 'q', value: string): void {
		if (key === 'user') {
			clearTimeout(userTimer);
			userTimer = setTimeout(() => update({ user: value.trim() }), 300);
		} else {
			clearTimeout(searchTimer);
			searchTimer = setTimeout(() => update({ q: value.trim() }), 300);
		}
	}
	function setScheduled(checked: boolean): void {
		clearTimeout(userTimer);
		update({ scheduled: checked ? 'true' : '', user: '' });
	}
	function midnight(day: string, nextDay: boolean): string {
		const date = new SvelteDate(`${day}T00:00:00`);
		if (nextDay) date.setDate(date.getDate() + 1);
		return Number.isNaN(date.getTime()) ? day : date.toISOString();
	}
	const apiUrl = $derived.by(() => {
		const query = new SvelteURLSearchParams();
		for (const key of keys) {
			const value = params.get(key);
			if (value && key !== 'page' && !(key === 'user' && scheduled))
				query.set(key, key === 'from' || key === 'to' ? midnight(value, key === 'to') : value);
		}
		query.set('page', String(currentPage));
		query.set('pageSize', '100');
		return `/api/admin/audit?${query}`;
	});
	$effect(() => {
		if (!auth.isAdmin) return;
		void reload;
		const controller = new AbortController();
		optionsError = null;
		Promise.all([
			api.get<string[]>('/api/admin/audit/actions', { signal: controller.signal }),
			api.get<SystemInfo[]>('/api/systems', { signal: controller.signal })
		]).then(
			([a, s]) => {
				actions = a;
				systems = s;
			},
			(e) => {
				if (!controller.signal.aborted) optionsError = e;
			}
		);
		return () => controller.abort();
	});
	$effect(() => {
		if (!auth.isAdmin) return;
		const url = apiUrl;
		void reload;
		const controller = new AbortController();
		result = null;
		error = null;
		api.get<Page<AuditItem>>(url, { signal: controller.signal }).then(
			(p) => {
				if (!controller.signal.aborted) result = p;
			},
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Auditing · RnRomManager</title></svelte:head>

<AdminOnly>
	<h2 class="h4">Auditing</h2>
	<div class="row g-2 mb-3">
		<div class="col-sm-6 col-lg-3">
			<label for="audit-action" class="form-label">Action</label>
			<select
				id="audit-action"
				class="form-select"
				value={params.get('action') ?? ''}
				onchange={(e) => update({ action: e.currentTarget.value })}
			>
				<option value="">All actions</option>
				{#each actions as action (action)}<option value={action}>{action}</option>{/each}
			</select>
		</div>
		<div class="col-sm-6 col-lg-3">
			<label for="audit-system" class="form-label">System</label>
			<select
				id="audit-system"
				class="form-select"
				value={params.get('system') ?? ''}
				onchange={(e) => update({ system: e.currentTarget.value })}
			>
				<option value="">All systems</option>
				{#each systems as system (system.id)}<option value={system.id}>{system.displayName}</option
					>{/each}
			</select>
		</div>
		<div class="col-sm-6 col-lg-3">
			<label for="audit-user" class="form-label">User</label>
			<input
				id="audit-user"
				class="form-control"
				value={scheduled ? '' : (params.get('user') ?? '')}
				disabled={scheduled}
				oninput={(e) => textInput('user', e.currentTarget.value)}
			/>
			<div class="form-check mt-1">
				<input
					id="audit-scheduled"
					type="checkbox"
					class="form-check-input"
					checked={scheduled}
					onchange={(e) => setScheduled(e.currentTarget.checked)}
				/><label for="audit-scheduled" class="form-check-label">Scheduled only</label>
			</div>
		</div>
		<div class="col-sm-6 col-lg-3">
			<label for="audit-search" class="form-label">Search</label>
			<input
				id="audit-search"
				type="search"
				class="form-control"
				maxlength="200"
				value={params.get('q') ?? ''}
				oninput={(e) => textInput('q', e.currentTarget.value)}
			/>
		</div>
		<div class="col-sm-4">
			<label for="audit-from" class="form-label">From</label><input
				id="audit-from"
				type="date"
				class="form-control"
				value={params.get('from') ?? ''}
				onchange={(e) => update({ from: e.currentTarget.value })}
			/>
		</div>
		<div class="col-sm-4">
			<label for="audit-to" class="form-label">To</label><input
				id="audit-to"
				type="date"
				class="form-control"
				value={params.get('to') ?? ''}
				onchange={(e) => update({ to: e.currentTarget.value })}
			/>
		</div>
		<div class="col-sm-4 d-flex align-items-end">
			<button
				class="btn btn-outline-secondary"
				disabled={!keys.some((key) => params.has(key))}
				onclick={clear}>Clear</button
			>
		</div>
	</div>
	{#if optionsError}<ErrorAlert error={optionsError} onretry={() => reload++} />{/if}
	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else if result.items.length === 0}
		<div class="card"><EmptyState message="No audit events found." /></div>
	{:else}
		<div class="card">
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							<th scope="col">Time</th>
							<th scope="col">Action</th>
							<th scope="col">System</th>
							<th scope="col">User</th>
							<th scope="col">Game</th>
							<th scope="col">Path</th>
							<th scope="col">Target</th>
							<th scope="col">Details</th>
						</tr>
					</thead>
					<tbody
						>{#each result.items as item (item.id)}<tr>
								<td class="text-nowrap">{formatDateTime(item.occurredAt)}</td>
								<td>{item.action}</td>
								<td
									>{#if item.system}<SystemBadge
											system={item.system}
											name={systems.find((s) => s.id === item.system)?.displayName}
										/>{:else}—{/if}</td
								>
								<td
									>{#if item.user}{item.user}{:else}<em>System</em>{/if}</td
								>
								<td
									>{#if item.gameId}<a href={resolve('/games/[id]', { id: item.gameId })}
											>{item.gameId}</a
										>{:else}—{/if}</td
								>
								<td class="text-truncate" style="max-width: 32rem" title={item.path ?? undefined}
									>{item.path ?? '—'}</td
								>
								<td
									class="text-truncate"
									style="max-width: 32rem"
									title={item.targetPath ?? undefined}>{item.targetPath ?? '—'}</td
								>
								<td>{item.details ?? '—'}</td>
							</tr>{/each}</tbody
					>
				</table>
			</div>
		</div>
	{/if}
	{#if result}<Pagination
			page={currentPage}
			pages={pageCount(result.total, 100)}
			onpage={(p) => update({ page: String(p) })}
		/>{/if}
</AdminOnly>
