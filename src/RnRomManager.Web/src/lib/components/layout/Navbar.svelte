<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import { afterNavigate } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { Collapse } from '#lib/bootstrap.js';
	import type { Me } from '#lib/types/index.js';
	import { formatCount } from '#lib/format.js';
	import Icon from '#lib/icons/Icon.svelte';
	import { theme } from '#lib/theme.svelte.js';
	import type { ThemeChoice } from '#lib/enums/index.js';
	import { romTools } from './RomToolsTabs.svelte';

	interface Props {
		me: Me;
		/** Pending review items (Admin); null until loaded. */
		reviewCount: number | null;
		onlogout: () => void;
		/** Opens the global search; the button is hidden while the password must be changed. */
		onsearch?: () => void;
		/** Bundle Creator B1: files in the working cart; the cart link shows when set and the password is changed. */
		cartCount?: number;
	}

	let { me, reviewCount, onlogout, onsearch, cartCount }: Props = $props();

	// B5: the link's name is hidden "Bundle" plus the visible badge plus hidden words ("Bundle 2 files in the cart"),
	// so axe's label-content-name-mismatch holds: the visible count is part of the accessible name.
	const cartFiles = $derived(cartCount === 1 ? 'file' : 'files');
	const isAdmin = $derived(me.roles.includes('admin'));

	/** B6: the theme choice lives in the user menu. */
	const themes: { value: ThemeChoice; label: string }[] = [
		{ value: 'light', label: 'Light' },
		{ value: 'dark', label: 'Dark' },
		{ value: 'auto', label: 'Auto' }
	];

	type NavLink = {
		// The links this bar has: a union of every page route is too wide for resolve()'s overloads.
		href: '/' | '/collections' | '/timeline' | '/reports' | '/ingestion';
		label: string;
		icon: string;
		admin?: boolean;
		/** Pages that also make this link active (the ROM Tools tabs). */
		section?: readonly string[];
	};

	const navigation: NavLink[] = [
		{ href: '/', label: 'Library', icon: 'library' },
		{ href: '/collections', label: 'Collections', icon: 'collections' },
		{ href: '/timeline', label: 'Timeline', icon: 'timeline' },
		{ href: '/reports', label: 'Reports', icon: 'reports' },
		{
			href: '/ingestion',
			label: 'ROM Tools',
			icon: 'tools',
			admin: true,
			section: romTools.map((tool) => tool.href)
		}
	];
	const visibleNavigation = $derived(navigation.filter((item) => !item.admin || isAdmin));
	const adminActive = $derived(isActive('/admin/users'));

	/** U1: the phone menu closes after a navigation; on desktop the collapse is never `.show`. */
	let collapse = $state<HTMLDivElement | null>(null);
	function hideMenu(): void {
		if (collapse?.classList.contains('show'))
			Collapse.getOrCreateInstance(collapse, { toggle: false }).hide();
	}
	afterNavigate(hideMenu);

	/** Any link but a dropdown toggle (dropdown items included) closes the menu. */
	function onMenuClick(e: MouseEvent): void {
		const link = (e.target as Element).closest('a');
		if (link && !link.matches('[data-bs-toggle="dropdown"]')) hideMenu();
	}

	function isActive(href: string): boolean {
		const path = page.url.pathname;
		if (href === '/') return path === '/' || path.startsWith('/games/');
		return path === href || path.startsWith(href.split('/').slice(0, 2).join('/') + '/');
	}
</script>

<!-- B26: sticky; modals (1055) and dropdowns still render above it (sticky-top is 1020). -->
<nav class="navbar navbar-expand-lg bg-body-tertiary border-bottom sticky-top">
	<div class="container">
		<a
			class="navbar-brand d-flex align-items-center"
			href={resolve('/')}
			aria-label="RnRomManager home"
		>
			<Icon name="system-gb" />
		</a>
		{#if onsearch && !me.mustChangePassword}
			<!-- Release editing B30: below lg, search sits outside the collapsed menu. -->
			<Button
				variant="link"
				class="nav-link ms-auto me-2 d-lg-none"
				icon="search"
				aria-label="Search"
				title="Search (Ctrl+K)"
				onclick={onsearch}
			/>
		{/if}
		<Button
			class="navbar-toggler"
			data-bs-toggle="collapse"
			data-bs-target="#main-nav"
			aria-controls="main-nav"
			aria-expanded="false"
			aria-label="Toggle navigation"><span class="navbar-toggler-icon"></span></Button
		>
		<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions (delegated from its links) -->
		<div class="collapse navbar-collapse" id="main-nav" bind:this={collapse} onclick={onMenuClick}>
			<ul class="navbar-nav me-auto">
				{#each visibleNavigation as item (item.label)}
					<li class="nav-item">{@render navigationLink(item)}</li>
				{/each}
			</ul>
			<ul class="navbar-nav align-items-lg-center navigation-utilities">
				{#if cartCount !== undefined && !me.mustChangePassword}
					<li class="nav-item">
						<a
							class="nav-link d-flex align-items-center gap-1"
							class:active={isActive('/bundle-creator')}
							aria-current={isActive('/bundle-creator') ? 'page' : undefined}
							href={resolve('/bundle-creator')}
							title="Bundle"
						>
							<!-- eslint-disable-next-line svelte/no-useless-mustaches -- Svelte trims a literal space at the start of a block -->
							<Icon name="cart" /><span class="visually-hidden">Bundle</span>{#if cartCount}{' '}
								<span class="badge rounded-pill text-bg-primary">{formatCount(cartCount)}</span>
								<span class="visually-hidden">{cartFiles} in the cart</span>
							{/if}
						</a>
					</li>
				{/if}
				{#if onsearch && !me.mustChangePassword}
					<li class="nav-item d-none d-lg-block">
						<!-- B4: Ctrl+K is reserved for search (hotkeys.ts), so the hint names it. -->
						<Button
							variant="link"
							class="nav-link d-flex align-items-center"
							icon="search"
							aria-label="Search"
							title="Search (Ctrl+K)"
							onclick={onsearch}
						/>
					</li>
				{/if}
				<li class="nav-item dropdown user-menu">
					<Button
						variant="link"
						class="nav-link dropdown-toggle d-flex align-items-center gap-1"
						icon="user"
						data-bs-toggle="dropdown"
						aria-expanded="false">{me.username}</Button
					>
					<ul class="dropdown-menu dropdown-menu-end">
						<!-- Release editing B31: below lg the menu shows inline, under the username. -->
						<li class="d-lg-none">
							<h6 class="dropdown-header d-flex align-items-center gap-2">
								<Icon name="user" />
								{me.username}
							</h6>
						</li>
						{#if isAdmin}
							<li>
								<a
									class="dropdown-item d-flex align-items-center gap-2"
									class:active={adminActive}
									aria-current={adminActive ? 'page' : undefined}
									href={resolve('/admin/users')}
								>
									<Icon name="admin" /> Admin
								</a>
							</li>
							<li><hr class="dropdown-divider" /></li>
						{/if}
						<li><h6 class="dropdown-header">Theme</h6></li>
						{#each themes as option (option.value)}
							<li>
								<Button
									icon="theme-{option.value}"
									aria-pressed={theme.choice === option.value}
									onclick={() => theme.set(option.value)}
									class={[
										'dropdown-item d-flex align-items-center gap-2',
										theme.choice === option.value && 'active'
									]
										.filter(Boolean)
										.join(' ')}>{option.label}</Button
								>
							</li>
						{/each}
						<li><hr class="dropdown-divider" /></li>
						<li>
							<a
								class="dropdown-item d-flex align-items-center gap-2"
								href={resolve('/change-password')}
							>
								<Icon name="password" /> Change password
							</a>
						</li>
						<li><hr class="dropdown-divider" /></li>
						<li>
							<Button
								icon="logout"
								class="dropdown-item d-flex align-items-center gap-2"
								onclick={onlogout}>Logout</Button
							>
						</li>
					</ul>
				</li>
			</ul>
		</div>
	</div>
</nav>

{#snippet navigationLink(link: NavLink)}
	{@const active = link.section ? link.section.some(isActive) : isActive(link.href)}
	<a
		class="nav-link d-flex align-items-center gap-1"
		class:active
		aria-current={active ? 'page' : undefined}
		href={resolve(link.href)}
	>
		<Icon name={link.icon} />
		{link.label}
		{#if link.section?.includes('/review') && reviewCount}
			<span class="badge rounded-pill text-bg-danger">
				{formatCount(reviewCount)}<span class="visually-hidden">&#32;pending review</span>
			</span>
		{/if}
	</a>
{/snippet}

<style>
	@media (max-width: 991.98px) {
		.navbar-collapse:global(.show) {
			max-height: calc(100dvh - 4rem);
			overflow-y: auto;
		}

		.navigation-utilities {
			margin-top: 0.5rem;
			padding-top: 0.5rem;
			border-top: 1px solid var(--bs-border-color);
		}

		/* Release editing B31: the user menu is always open inline; its toggle goes. */
		.user-menu > :global(.dropdown-toggle) {
			display: none;
		}

		.user-menu > :global(.dropdown-menu) {
			display: block;
			position: static;
			border: 0;
			background: transparent;
		}
	}
</style>
