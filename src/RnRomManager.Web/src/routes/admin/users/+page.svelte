<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { UserInfo } from '#lib/types/index.js';
	import type { Role } from '#lib/enums/index.js';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { passwordError, policyHint, usernameError } from '#lib/passwordPolicy.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	let users = $state<UserInfo[] | null>(null);
	let error = $state<unknown>(null);

	let newUsername = $state('');
	/** U1: the two account types, mapped to the API's `roles`. The create form starts on Site user. */
	type AccountType = 'admin' | 'site-user';
	const typeLabels: Record<AccountType, string> = { admin: 'Admin', 'site-user': 'Site user' };
	const typeOf = (user: UserInfo): AccountType =>
		user.roles.includes('admin') ? 'admin' : 'site-user';
	const rolesOf = (type: AccountType): Role[] => (type === 'admin' ? ['admin'] : []);
	const selfHint = "You can't change your own account type or disable yourself";
	let newType = $state<AccountType>('site-user');
	let newPassword = $state('');
	let createOpen = $state(false);
	let createErrors = $state<{ username?: string; password?: string; roles?: string }>({});
	let creating = $state(false);

	let resetUser = $state<UserInfo | null>(null);
	let resetOpen = $state(false);
	let resetPassword = $state('');
	let resetError = $state<string | undefined>();
	let resetting = $state(false);

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
		newUsername = '';
		newPassword = '';
		newType = 'site-user';
		createErrors = {};
		createOpen = true;
	}

	async function create(event?: SubmitEvent): Promise<void> {
		event?.preventDefault();
		if (creating) return;
		const username = newUsername.trim();
		createErrors = {
			username: usernameError(username),
			password: passwordError(newPassword, username)
		};
		if (createErrors.username || createErrors.password) return;
		creating = true;
		try {
			const user = await api.post<UserInfo>('/api/users', {
				username,
				roles: rolesOf(newType),
				password: newPassword
			});
			createOpen = false;
			toasts.show(
				`${user.username} created. They must change the password at first sign-in.`,
				'success'
			);
			await load();
		} catch (e) {
			if (isApiError(e, 'UsernameTaken')) createErrors = { username: 'That username is taken.' };
			else if (isApiError(e, 'ValidationFailed'))
				createErrors = {
					username: e.errors.username?.[0],
					password: e.errors.password?.[0],
					roles: e.errors.roles?.[0]
				};
			else failed(e);
		} finally {
			creating = false;
		}
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
		resetPassword = '';
		resetError = undefined;
		resetOpen = true;
	}

	async function reset(): Promise<void> {
		if (!resetUser) return;
		resetError = passwordError(resetPassword, resetUser.username);
		if (resetError) return;
		resetting = true;
		try {
			await api.post(`/api/users/${resetUser.id}/reset-password`, { newPassword: resetPassword });
			resetOpen = false;
			toasts.show(
				`Password reset. ${resetUser.username} must change it at next sign-in.`,
				'success'
			);
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) resetError = e.errors.newPassword?.[0] ?? e.title;
			else {
				resetOpen = false;
				failed(e);
			}
		} finally {
			resetting = false;
		}
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

	<ConfirmModal
		bind:open={createOpen}
		title="Create user"
		confirmLabel="Create"
		size="lg"
		busy={creating}
		onconfirm={() => create()}
	>
		<form class="row g-3" onsubmit={create} novalidate>
			<div class="col-12 col-md-6">
				<label class="form-label" for="new-username">Username</label>
				<input
					id="new-username"
					class="form-control"
					class:is-invalid={createErrors.username}
					autocomplete="off"
					autocapitalize="none"
					spellcheck="false"
					bind:value={newUsername}
				/>
				{#if createErrors.username}<div class="invalid-feedback">{createErrors.username}</div>{/if}
			</div>
			<div class="col-12 col-md-6">
				<label class="form-label" for="new-password">Initial password</label>
				<input
					id="new-password"
					type="password"
					class="form-control"
					class:is-invalid={createErrors.password}
					autocomplete="new-password"
					aria-describedby="new-password-help"
					bind:value={newPassword}
				/>
				{#if createErrors.password}<div class="invalid-feedback">{createErrors.password}</div>{/if}
				<div id="new-password-help" class="form-text">{policyHint}</div>
			</div>
			<div class="col-12 col-md-6">
				<label class="form-label" for="new-type">Account type</label>
				<select
					id="new-type"
					class="form-select"
					class:is-invalid={createErrors.roles}
					bind:value={newType}
				>
					{#each Object.entries(typeLabels) as [value, label] (value)}
						<option {value}>{label}</option>
					{/each}
				</select>
				{#if createErrors.roles}<div class="invalid-feedback">{createErrors.roles}</div>{/if}
			</div>
			<!-- Enter in a field submits; the visible button is the modal's Create. -->
			<button type="submit" class="d-none" tabindex="-1" aria-hidden="true"></button>
		</form>
	</ConfirmModal>

	<ConfirmModal
		bind:open={resetOpen}
		title="Reset password"
		confirmLabel="Reset password"
		busy={resetting}
		onconfirm={reset}
	>
		<p>
			Set a new password for {resetUser?.username}. They must change it at next sign-in, and their
			lockout is cleared.
		</p>
		<label class="form-label" for="reset-password">New password</label>
		<input
			id="reset-password"
			type="password"
			class="form-control"
			class:is-invalid={resetError}
			autocomplete="new-password"
			aria-describedby="reset-password-help"
			bind:value={resetPassword}
		/>
		{#if resetError}<div class="invalid-feedback">{resetError}</div>{/if}
		<div id="reset-password-help" class="form-text">{policyHint}</div>
	</ConfirmModal>
{/if}
