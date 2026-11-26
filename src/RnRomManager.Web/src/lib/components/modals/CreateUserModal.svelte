<script lang="ts">
	import { untrack } from 'svelte';
	import BaseModal from './BaseModal.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import Select from '#lib/components/forms/Select.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import { passwordError, policyHint, usernameError } from '#lib/passwordPolicy.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import type { UserInfo } from '#lib/types/index.js';
	import type { Role } from '#lib/enums/index.js';

	interface Props {
		/** Opening starts an empty form on Site user. */
		open: boolean;
		/** U1: the account type labels (`admin`, `site-user`). */
		typeLabels: Record<'admin' | 'site-user', string>;
		/** After the user is created (the toast is shown here). */
		oncreated: (user: UserInfo) => void;
		/** Any other failure (LastAdmin, network…), after which the modal stays open. */
		onfailed: (e: unknown) => void;
	}

	let { open = $bindable(), typeLabels, oncreated, onfailed }: Props = $props();

	type AccountType = 'admin' | 'site-user';
	const rolesOf = (type: AccountType): Role[] => (type === 'admin' ? ['admin'] : []);

	let username = $state('');
	let password = $state('');
	let type = $state<AccountType>('site-user');
	let errors = $state<{ username?: string; password?: string; roles?: string }>({});
	let creating = $state(false);

	$effect(() => {
		if (!open) return;
		untrack(() => {
			username = '';
			password = '';
			type = 'site-user';
			errors = {};
		});
	});

	async function create(event?: SubmitEvent): Promise<void> {
		event?.preventDefault();
		if (creating) return;
		const name = username.trim();
		errors = { username: usernameError(name), password: passwordError(password, name) };
		if (errors.username || errors.password) return;
		creating = true;
		try {
			const user = await api.post<UserInfo>('/api/users', {
				username: name,
				roles: rolesOf(type),
				password
			});
			open = false;
			toasts.show(
				`${user.username} created. They must change the password at first sign-in.`,
				'success'
			);
			oncreated(user);
		} catch (e) {
			if (isApiError(e, 'UsernameTaken')) errors = { username: 'That username is taken.' };
			else if (isApiError(e, 'ValidationFailed'))
				errors = {
					username: e.errors.username?.[0],
					password: e.errors.password?.[0],
					roles: e.errors.roles?.[0]
				};
			else onfailed(e);
		} finally {
			creating = false;
		}
	}

	const invalid = (message: string | undefined) => (message ? 'is-invalid' : undefined);
</script>

<BaseModal
	bind:open
	title="Create user"
	confirmLabel="Create"
	size="lg"
	busy={creating}
	onconfirm={() => create()}
>
	<form class="row g-3" onsubmit={create} novalidate>
		<div class="col-12 col-md-6">
			<FormField label="Username" for="new-username">
				<TextInput
					id="new-username"
					class={invalid(errors.username)}
					autocomplete="off"
					autocapitalize="none"
					spellcheck="false"
					bind:value={username}
				/>
				{#if errors.username}<div class="invalid-feedback">{errors.username}</div>{/if}
			</FormField>
		</div>
		<div class="col-12 col-md-6">
			<FormField label="Initial password" for="new-password">
				<TextInput
					id="new-password"
					type="password"
					class={invalid(errors.password)}
					autocomplete="new-password"
					aria-describedby="new-password-help"
					bind:value={password}
				/>
				{#if errors.password}<div class="invalid-feedback">{errors.password}</div>{/if}
				<div id="new-password-help" class="form-text">{policyHint}</div>
			</FormField>
		</div>
		<div class="col-12 col-md-6">
			<FormField label="Account type" for="new-type">
				<Select
					id="new-type"
					class={invalid(errors.roles)}
					bind:value={type}
					options={Object.entries(typeLabels).map(([value, label]) => ({ value, label }))}
				/>
				{#if errors.roles}<div class="invalid-feedback">{errors.roles}</div>{/if}
			</FormField>
		</div>
		<!-- Enter in a field submits; the visible button is the modal's Create. -->
		<Button type="submit" class="d-none" tabindex={-1} aria-hidden="true" />
	</form>
</BaseModal>
