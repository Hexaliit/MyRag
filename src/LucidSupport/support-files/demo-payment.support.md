---
page_id: demo-payment
url_pattern: /demo/payment*
title: Payment Details
learned: 2026-02-04T00:00:00Z
site: localhost
nav:
  back: { label: "Back", selector: "#back-btn" }
  next: { label: "Pay Now", selector: "#pay-btn" }
flow: demo-checkout
step: 2
prev: demo-contact
---

# Payment Details

Demo payment form with 6 fields: card number, expiry, CVV, billing address, city, and postal code.
Uses input masking for card number and expiry. All fields are required.

## Fields

### [#card-number] Card Number
- type: text
- label: Card Number
- placeholder: 4242 4242 4242 4242
- pattern: credit-card
- autocomplete: cc-number
- required: true
- maxLength: 19
- validation:
  - client: required, pattern(13-19 digits)
- errors:
  - required: "Card Number is required"
  - pattern: "Please enter a valid card number (13-19 digits)"
- help: Enter your 16-digit card number. We accept Visa, Mastercard, and Amex. The number will be formatted automatically with spaces.

### [#expiry] Expiry Date
- type: text
- label: Expiry Date
- placeholder: MM/YY
- pattern: date-partial
- autocomplete: cc-exp
- required: true
- maxLength: 5
- validation:
  - client: required, pattern(MM/YY)
- errors:
  - required: "Expiry Date is required"
  - pattern: "Please use MM/YY format"
  - month: "Month must be 01-12"
- help: Enter the expiration date from the front of your card in MM/YY format (e.g., 03/27).

### [#cvv] CVV
- type: text
- label: CVV
- placeholder: 123
- pattern: cvv
- autocomplete: cc-csc
- required: true
- maxLength: 4
- validation:
  - client: required, pattern(3-4 digits)
- errors:
  - required: "CVV is required"
  - pattern: "CVV must be 3 or 4 digits"
- help: The 3-digit code on the back of your Visa/Mastercard, or the 4-digit code on the front of your Amex card.

### [#billing-address] Billing Address
- type: text
- label: Billing Address
- placeholder: 123 Main Street
- pattern: address
- autocomplete: address-line1
- required: true
- minLength: 5
- validation:
  - client: required, minlength(5)
- errors:
  - required: "Billing Address is required"
  - minlength: "Address must be at least 5 characters"
- help: Enter the street address associated with your credit card billing statement.

### [#billing-city] City
- type: text
- label: City
- autocomplete: address-level2
- placeholder: San Francisco
- required: true
- minLength: 2
- validation:
  - client: required, minlength(2)
- errors:
  - required: "City is required"
  - minlength: "City must be at least 2 characters"
- help: The city of your billing address.

### [#billing-postal] Postal Code
- type: text
- label: Postal Code
- placeholder: 94102
- pattern: postal-code
- autocomplete: postal-code
- required: true
- validation:
  - client: required, pattern
- errors:
  - required: "Postal Code is required"
  - pattern: "Please enter a valid postal or ZIP code"
- help: Your ZIP or postal code. Accepted formats: US (12345 or 12345-6789), UK (SW1A 1AA), CA (K1A 0B1).

## Conditions

> when: [#card-number].error
> suggest: Enter your 16-digit card number. We accept Visa, Mastercard, and Amex. Numbers are formatted automatically.
> highlight: #card-number

> when: [#cvv].error
> suggest: The CVV is a security code: 3 digits on the back for Visa/Mastercard, or 4 digits on the front for Amex.
> highlight: #cvv

> when: [#expiry].error
> suggest: Enter the expiry date from your card in MM/YY format, e.g. 03/27.
> highlight: #expiry

> when: [#billing-address].error
> suggest: Enter the street address that matches your card's billing statement.
> highlight: #billing-address

> when: page.idle > 30s AND form.incomplete
> suggest: Need help completing your payment? I can walk you through each field.

> when: user.attempts > 1
> suggest: Having trouble? Make sure your card number, expiry, and CVV all match what's on your card.

## Topics

- "What payment methods do you accept?" -> accepted-payment-methods
- "Is my payment secure?" -> security-and-encryption
- "Where do I find my CVV?" -> cvv-location
- "Why was my card declined?" -> card-declined-reasons
- "Can I use a different billing address?" -> billing-address-info
