// =============================================================================================
// The error-rate alert, as a scheduled query rule.
//
// ---------------------------------------------------------------------------------------------
// WHY AN ERROR *RATE* AND NOT AN ERROR *COUNT*
//
// A count threshold is the obvious choice and it is wrong in both directions at once.
//
// Ten failures a minute is a catastrophe for a service doing twenty requests a minute, and noise
// for one doing fifty thousand. A count alert therefore has to be retuned every time traffic
// changes, and in practice it is not — so it either screams during a traffic spike that is
// working fine, or stays silent through an outage that happens overnight when volume is low.
//
// A rate is dimensionless. Five percent means the same thing at any traffic level, which is what
// lets the threshold outlive the traffic assumptions that were true when it was written.
//
// ---------------------------------------------------------------------------------------------
// THE GUARD THAT MATTERS MOST
//
//   | where Total > 10
//
// Without it, one failed request in an idle minute is a 100% error rate and the alert fires. At
// 3am, on a dev environment, on the health probe. That single line is the difference between an
// alert people act on and an alert people mute — and a muted alert is worse than no alert,
// because it still looks like coverage on a dashboard.
// =============================================================================================

param location string
param appInsightsId string
param thresholdPercent int
param tags object

@description('How often the rule runs, ISO 8601.')
param evaluationFrequency string = 'PT5M'

@description('Lookback per evaluation. Must be >= evaluationFrequency.')
param windowSize string = 'PT15M'

@description('Minimum requests in the window before the rate is trusted. See the header.')
param minimumRequests int = 10

var ruleName = 'alert-quotes-error-rate'

// ---------------------------------------------------------------------------------------------
// The query.
//
// `success == false` rather than `resultCode >= 500`, because a 4xx storm is also an outage from
// the caller's point of view — an expired signing key returning 401 to every client is not "fine,
// those are client errors". The SDK sets `success` from the status code and the request outcome,
// which is closer to "did the caller get what they asked for" than the status code alone.
//
// The window is 15 minutes against a 5-minute cadence, so each evaluation overlaps the previous
// two. A brief spike is therefore seen by three consecutive evaluations rather than falling
// between two — the standard way of making a rate alert insensitive to exactly where a burst
// lands relative to the clock.
// ---------------------------------------------------------------------------------------------
var errorRateQuery = '''
requests
| summarize Total = count(), Failed = countif(success == false)
| where Total > MINIMUM_REQUESTS
| project ErrorRatePercent = round(100.0 * Failed / Total, 2)
'''

resource rule 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: ruleName
  location: location
  tags: tags
  properties: {
    displayName: ruleName
    description: 'Fires when more than ${thresholdPercent}% of requests fail over ${windowSize}, ignoring windows with fewer than ${minimumRequests} requests.'
    enabled: true

    // Severity 2 = Warning. Not 0 (Critical): this is an error-rate signal on a dev environment,
    // and reserving the top severities for things that genuinely wake somebody is what keeps
    // them meaningful.
    severity: 2

    scopes: [
      appInsightsId
    ]
    evaluationFrequency: evaluationFrequency
    windowSize: windowSize

    criteria: {
      allOf: [
        {
          query: replace(errorRateQuery, 'MINIMUM_REQUESTS', string(minimumRequests))
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'ErrorRatePercent'
          operator: 'GreaterThan'
          threshold: thresholdPercent

          // One consecutive breach is enough to fire, because the 15-minute window already
          // smooths the signal. Requiring several would delay a real incident by 15 minutes
          // per extra evaluation for no additional confidence.
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }

    // Resolves itself when the rate drops back under the threshold, rather than leaving a stale
    // alert that somebody has to close by hand. An alert list full of already-fixed incidents is
    // how real ones get missed.
    autoMitigate: true

    // ---------------------------------------------------------------------------------------
    // NO ACTION GROUP, and this is a real limitation rather than an oversight.
    //
    // An action group is what turns a fired alert into an email, an SMS or a webhook. One is not
    // created here because every useful action group contains a personal contact detail, and
    // committing a real address to a repository is exactly the kind of thing this codebase has
    // spent three days avoiding.
    //
    // The rule still evaluates, still fires, and its state is still visible in the portal and
    // via `az monitor scheduled-query show` — which is what Day26/scripts/verify-alert.sh
    // checks. Adding notification is one property:
    //
    //   actions: { actionGroups: [ '<action-group-resource-id>' ] }
    // ---------------------------------------------------------------------------------------
    actions: {}
  }
}

output ruleName string = rule.name
output ruleId string = rule.id
