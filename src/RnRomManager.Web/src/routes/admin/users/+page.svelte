<script lang="ts">
	import ResetPasswordModal from '#lib/components/modals/ResetPasswordModal.svelte';
	import CreateUserModal from '#lib/components/modals/CreateUserModal.svelte';
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { UserInfo } from '#lib/types/index.js';
	import type { Role } from '#lib/enums/index.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	let users = $state<UserInfo[] | null>(null);
	let error = $state<unknown>(null);

	/** U1: the two account types, mapped to the API's `roles`. The create form starts on Site user. */
	type AccountType = 'admin' | 'site-user';
	const typeLabels: Record<AccountType, string> = { admin: 'Admin', 'site-user': 'Site user' };
	const typeOf = (user: UserInfo): AccountType =>
		user.roles.includes('admin') ? 'admin' : 'site-user';
	const rolesOf = (type: AccountType): Role[] => (type === 'admin' ? ['admin'] : []);
	const selfHint = "You can't change your own account type or disable yourself";
	let createOpen = $state(false);

	let resetUser = $state<UserInfo | null>(null);
	let resetOpen = $state(false);

	async function load(): Promise<void> {
		error = null;
		try {
			users = await api.get<UserInfo[]>('/api/users');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (auth.isAdmin) void load();
	});

	function failed(e: unknown): void {
		if (isApiError(e, 'LastAdmin'))
			toasts.show("The last enabled Admin can't be disabled or demoted.", 'warning');
		else if (isApiError(e, 'SelfChange')) toasts.show(e.title, 'warning');
		else toasts.show(isApiError(e) ? e.title : 'Something went wrong.', 'danger');
	}

	function openCreate(): void {
		createOpen = true;
	}

	async function update(
		user: UserInfo,
		changes: { roles?: Role[]; isDisabled?: boolean }
	): Promise<void> {
		try {
			const updated = await api.patch<UserInfo>(`/api/users/${user.id}`, changes);
			users = users?.map((u) => (u.id === updated.id ? updated : u)) ?? null;
		} catch (e) {
			failed(e);
			await load(); // put the type select back
		}
	}

	function openReset(user: UserInfo): void {
		resetUser = user;
		resetOpen = true;
	}
</script>

<svelte:head><title>Users · Admin · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !users}
	<Loading />
{:else}
	<div class="d-flex justify-content-end mb-3">
		<button type="button" class="btn btn-primary" onclick={openCreate}>
			<Icon name="add-user" /> Create user
		</button>
	</div>
	<div class="card">
		<div class="table-responsive">
			{#if users.length === 0}
				<EmptyState message="No users." />
			{:else}
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr class="small text-uppercase text-body-secondary">
							<th scope="col">Username</th>
							<th scope="col">Type</th>
							<th scope="col">Status</th>
							<th scope="col">Last sign-in</th>
							<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
						</tr>
					</thead>
					<tbody>
						{#each users as user (user.id)}
							{@const self = user.id === auth.me?.id}
							<tr>
								<td>{user.username}</td>
								<td>
									<span use:tooltip={self ? selfHint : ''}>
										<select
											class="form-select form-select-sm w-auto"
											aria-label="Account type of {user.username}"
											value={typeOf(user)}
											disabled={self}
											onchange={(e) =>
												update(user, { roles: rolesOf(e.currentTarget.value as AccountType) })}
										>
											{#each Object.entries(typeLabels) as [value, label] (value)}
												<option {value}>{label}</option>
											{/each}
										</select>
									</span>
								</td>
								<td>
									{#if user.isDisabled}
										<span class="badge text-bg-secondary">Disabled</span>
									{:else}
										<span class="badge text-bg-success">Enabled</span>
									{/if}
								</td>
								<td class="text-nowrap"
									>{user.lastLoginAt ? formatDateTime(user.lastLoginAt) : 'Never'}</td
								>
								<td class="text-nowrap text-end">
									<span class="d-inline-block" use:tooltip={self ? selfHint : ''}>
										<button
											type="button"
											class="btn btn-sm btn-outline-secondary"
											aria-label="{user.isDisabled ? 'Enable' : 'Disable'} {user.username}"
											disabled={self}
											onclick={() => update(user, { isDisabled: !user.isDisabled })}
										>
											<Icon name={user.isDisabled ? 'enable' : 'disable'} />
										</button>
									</span>
									<button
										type="button"
										class="btn btn-sm btn-outline-secondary"
										aria-label="Reset password of {user.username}"
										onclick={() => openReset(user)}
									>
										<Icon name="password" />
									</button>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			{/if}
		</div>
	</div>

	<CreateUserModal
		bind:open={createOpen}
		{typeLabels}
		oncreated={() => void load()}
		onfailed={failed}
	/>

	<ResetPasswordModal bind:open={resetOpen} user={resetUser} onfailed={failed} />
{/if}
