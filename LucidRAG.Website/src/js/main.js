// Pricing calculator component
document.addEventListener('alpine:init', () => {
    Alpine.data('pricingApp', () => ({
        billingPeriod: 'monthly',
        professionalFeatures: {
            oauth: false,
            multiTenant: false,
            budgetControls: false,
            unlimitedDocs: false,
            prioritySupport: false,
            distributedIngesters: false,
            distributedSearchers: false,
            analyticsDashboard: false
        },
        professionalPricing: {
            oauth: 25,
            multiTenant: 25,
            budgetControls: 10,
            unlimitedDocs: 15,
            prioritySupport: 20,
            distributedIngesters: 50,
            distributedSearchers: 75,
            analyticsDashboard: 15
        },
        presets: {
            none: [],
            basicPro: ['oauth', 'multiTenant', 'unlimitedDocs'],
            team: ['oauth', 'multiTenant', 'unlimitedDocs', 'distributedIngesters'],
            enterprise: ['oauth', 'multiTenant', 'unlimitedDocs', 'prioritySupport', 'distributedIngesters', 'distributedSearchers']
        },
        selectedPreset: 'none',

        get professionalTotal() {
            return Object.entries(this.professionalFeatures).reduce((total, [key, enabled]) => {
                return total + (enabled ? this.professionalPricing[key] : 0);
            }, 0);
        },

        get freePrice() {
            return 0;
        },

        get enterprisePrice() {
            return 'Contact for Pricing';
        },

        applyPreset(preset) {
            this.selectedPreset = preset;
            Object.keys(this.professionalFeatures).forEach(key => {
                this.professionalFeatures[key] = this.presets[preset].includes(key);
            });
        }
    }));

    Alpine.data('scrollSpy', () => ({
        activeSection: '',
        init() {
            this.observeSections();
        },
        observeSections() {
            const observer = new IntersectionObserver((entries) => {
                entries.forEach(entry => {
                    if (entry.isIntersecting) {
                        this.activeSection = entry.target.id;
                    }
                });
            }, { threshold: 0.3 });

            document.querySelectorAll('section[id]').forEach(section => {
                observer.observe(section);
            });
        }
    }));

    Alpine.data('mobileMenu', () => ({
        isOpen: false,
        toggle() {
            this.isOpen = !this.isOpen;
        }
    }));

    Alpine.data('featureComparison', () => ({
        showAll: false,
        visibleCount: 6,
        totalFeatures: 24,
        toggle() {
            this.showAll = !this.showAll;
        }
    }));
});
